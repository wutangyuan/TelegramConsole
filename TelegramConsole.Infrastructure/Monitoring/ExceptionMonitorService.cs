using System.Globalization;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using TelegramConsole.Core;

namespace TelegramConsole.Infrastructure;

public sealed class ExceptionMonitorService : IExceptionMonitorService
{
    private readonly IAppLogger _logger;
    private readonly ITelegramService _telegram;
    private readonly AppSettings _settings;
    private readonly Channel<long> _notificationQueue = Channel.CreateUnbounded<long>(new()
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly Task _notificationWorker;
    private long _activeUserId;
    private ExceptionAlertSettings? _activeAlerts;
    private bool _disposed;

    public string DatabasePath { get; }
    public event Action? RecordsChanged;

    public ExceptionMonitorService(
        ISettingsStore store,
        IAppLogger logger,
        ITelegramService telegram,
        AppSettings settings)
    {
        _logger = logger;
        _telegram = telegram;
        _settings = settings;
        DatabasePath = Path.Combine(store.DataDirectory, "exceptions.db");
        InitializeDatabase();
        _logger.EntryWritten += OnLogEntry;
        _notificationWorker = Task.Run(ProcessNotificationsAsync);
        _logger.Info("ExceptionMonitor", $"异常监控已启动，数据库：{DatabasePath}");
    }

    private void OnLogEntry(AppLogEntry entry)
    {
        if (_disposed || _activeUserId == 0 || entry.Level < AppLogLevel.Error || entry.Category == "ExceptionMonitor" ||
            string.IsNullOrWhiteSpace(entry.Exception)) return;
        try
        {
            var id = Insert(entry);
            RecordsChanged?.Invoke();
            if (ShouldNotify(_activeAlerts, entry.Level))
                _notificationQueue.Writer.TryWrite(id);
        }
        catch
        {
            // Monitoring must never cause a second application failure.
        }
    }

    private void InitializeDatabase()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS exception_logs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                account_id INTEGER NOT NULL DEFAULT 0,
                occurred_at TEXT NOT NULL,
                level TEXT NOT NULL,
                category TEXT NOT NULL,
                message TEXT NOT NULL,
                details TEXT NOT NULL,
                telegram_status TEXT NOT NULL DEFAULT '待处理',
                email_status TEXT NOT NULL DEFAULT '待处理'
            );
            CREATE INDEX IF NOT EXISTS ix_exception_logs_occurred_at
                ON exception_logs(occurred_at DESC);
            """;
        command.ExecuteNonQuery();
        using var schemaCommand = connection.CreateCommand();
        schemaCommand.CommandText = "PRAGMA table_info(exception_logs);";
        using var reader = schemaCommand.ExecuteReader();
        var hasAccountId = false;
        while (reader.Read())
            if (reader.GetString(1) == "account_id") hasAccountId = true;
        reader.Close();
        if (!hasAccountId)
        {
            using var migration = connection.CreateCommand();
            migration.CommandText = "ALTER TABLE exception_logs ADD COLUMN account_id INTEGER NOT NULL DEFAULT 0;";
            migration.ExecuteNonQuery();
        }
    }

    public void ActivateAccount(long userId, ExceptionAlertSettings settings)
    {
        _activeUserId = userId;
        _activeAlerts = settings;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE exception_logs SET account_id = $userId WHERE account_id = 0;";
        command.Parameters.AddWithValue("$userId", userId);
        command.ExecuteNonQuery();
        RecordsChanged?.Invoke();
    }

    public void DeactivateAccount()
    {
        _activeUserId = 0;
        _activeAlerts = null;
        RecordsChanged?.Invoke();
    }

    private long Insert(AppLogEntry entry)
    {
        var alert = _activeAlerts ?? new ExceptionAlertSettings { NotificationsEnabled = false };
        var telegramStatus = ChannelStatus(alert.NotificationsEnabled, alert.TelegramPeerId is not null, entry.Level, alert.MinimumLevel);
        var emailStatus = ChannelStatus(alert.EmailNotificationsEnabled, !string.IsNullOrWhiteSpace(alert.EmailRecipient), entry.Level, alert.MinimumLevel);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO exception_logs
                (account_id, occurred_at, level, category, message, details, telegram_status, email_status)
            VALUES
                ($accountId, $occurredAt, $level, $category, $message, $details, $telegramStatus, $emailStatus);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$accountId", _activeUserId);
        command.Parameters.AddWithValue("$occurredAt", entry.Timestamp.ToString("O"));
        command.Parameters.AddWithValue("$level", entry.Level.ToString());
        command.Parameters.AddWithValue("$category", entry.Category);
        command.Parameters.AddWithValue("$message", entry.Message);
        command.Parameters.AddWithValue("$details", entry.Exception ?? "");
        command.Parameters.AddWithValue("$telegramStatus", telegramStatus);
        command.Parameters.AddWithValue("$emailStatus", emailStatus);
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public Task<IReadOnlyList<ExceptionRecord>> GetRecentAsync(int limit = 200) =>
        QueryAsync(new ExceptionQuery(Limit: limit));

    public async Task<IReadOnlyList<ExceptionRecord>> QueryAsync(ExceptionQuery query)
    {
        if (_activeUserId == 0) return [];
        var limit = Math.Clamp(query.Limit, 1, 2000);
        var records = new List<ExceptionRecord>();
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        var conditions = new List<string> { "account_id = $accountId" };
        command.Parameters.AddWithValue("$accountId", _activeUserId);
        if (query.From is not null)
        {
            conditions.Add("occurred_at >= $from");
            command.Parameters.AddWithValue("$from", query.From.Value.ToString("O"));
        }
        if (query.ToExclusive is not null)
        {
            conditions.Add("occurred_at < $to");
            command.Parameters.AddWithValue("$to", query.ToExclusive.Value.ToString("O"));
        }
        if (query.Level is not null)
        {
            conditions.Add("level = $level");
            command.Parameters.AddWithValue("$level", query.Level.Value.ToString());
        }
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            conditions.Add("(category LIKE $keyword OR message LIKE $keyword OR details LIKE $keyword)");
            command.Parameters.AddWithValue("$keyword", $"%{query.Keyword.Trim()}%");
        }
        var where = conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions);
        command.CommandText = $"""
            SELECT id, occurred_at, level, category, message, details, telegram_status, email_status
            FROM exception_logs {where} ORDER BY id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            Enum.TryParse<AppLogLevel>(reader.GetString(2), out var level);
            records.Add(new ExceptionRecord(
                reader.GetInt64(0),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                level,
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7)));
        }
        return records;
    }

    public async Task RetryNotificationsAsync(IEnumerable<long> recordIds)
    {
        foreach (var id in recordIds.Distinct())
            await NotifyAsync(id);
    }

    public async Task SendTestNotificationAsync()
    {
        var alert = _activeAlerts ?? throw new InvalidOperationException("请先登录 Telegram 账号");
        if (!alert.NotificationsEnabled && !alert.EmailNotificationsEnabled)
            throw new InvalidOperationException("请先启用 Telegram 异常通知或异常邮件通知");

        var text = $"【Telegram 控制台异常通知测试】\n时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n设备：{Environment.MachineName}\n如果收到此消息，说明通知配置正常。";
        var attempts = 0;
        var errors = new List<string>();
        if (alert.NotificationsEnabled && alert.TelegramPeerId is long peerId)
        {
            attempts++;
            try
            {
                await _telegram.SendAsync(
                    new DialogItem(alert.TelegramPeerTitle, peerId, alert.TelegramPeerKind, alert.TelegramPeerKind != "User"),
                    text);
            }
            catch (Exception ex) { errors.Add("Telegram：" + ex.Message); }
        }
        if (alert.EmailNotificationsEnabled && !string.IsNullOrWhiteSpace(alert.EmailRecipient))
        {
            attempts++;
            try
            {
                await EmailNotificationService.SendAsync(
                    _settings.Email, alert.EmailRecipient, "Telegram 控制台异常通知测试", text);
            }
            catch (Exception ex) { errors.Add("邮件：" + ex.Message); }
        }
        if (attempts == 0) throw new InvalidOperationException("请为已启用的通知渠道配置目标");
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
    }

    private async Task ProcessNotificationsAsync()
    {
        await foreach (var id in _notificationQueue.Reader.ReadAllAsync())
        {
            try { await NotifyAsync(id); }
            catch { /* Per-channel failures are persisted by NotifyAsync. */ }
        }
    }

    private async Task NotifyAsync(long id)
    {
        var record = await GetByIdAsync(id);
        if (record is null) return;
        var alert = _activeAlerts ?? new ExceptionAlertSettings { NotificationsEnabled = false };
        var text = BuildNotification(record);

        var telegramStatus = "未配置";
        if (alert.NotificationsEnabled && alert.TelegramPeerId is long peerId)
        {
            try
            {
                await _telegram.SendAsync(
                    new DialogItem(alert.TelegramPeerTitle, peerId, alert.TelegramPeerKind, alert.TelegramPeerKind != "User"),
                    text);
                telegramStatus = "已发送";
            }
            catch (Exception ex) { telegramStatus = Failure(ex); }
        }

        var emailStatus = "未配置";
        if (alert.EmailNotificationsEnabled && !string.IsNullOrWhiteSpace(alert.EmailRecipient))
        {
            try
            {
                await EmailNotificationService.SendAsync(
                    _settings.Email,
                    alert.EmailRecipient,
                    $"Telegram 控制台异常：{record.Category}",
                    text);
                emailStatus = "已发送";
            }
            catch (Exception ex) { emailStatus = Failure(ex); }
        }

        await UpdateStatusesAsync(id, telegramStatus, emailStatus);
        RecordsChanged?.Invoke();
        _logger.Info("ExceptionMonitor", $"异常 #{id} 通知处理完成，Telegram={telegramStatus}，Email={emailStatus}");
    }

    private static bool ShouldNotify(ExceptionAlertSettings? alert, AppLogLevel level) =>
        alert is not null && level >= alert.MinimumLevel &&
        (alert.NotificationsEnabled && alert.TelegramPeerId is not null ||
         alert.EmailNotificationsEnabled && !string.IsNullOrWhiteSpace(alert.EmailRecipient));

    private static string ChannelStatus(bool enabled, bool configured, AppLogLevel level, AppLogLevel minimumLevel) =>
        !enabled ? "通知已关闭" :
        !configured ? "未配置" :
        level < minimumLevel ? "低于通知级别" : "待处理";

    private async Task<ExceptionRecord?> GetByIdAsync(long id)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, occurred_at, level, category, message, details, telegram_status, email_status
            FROM exception_logs WHERE id = $id AND account_id = $accountId;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$accountId", _activeUserId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        Enum.TryParse<AppLogLevel>(reader.GetString(2), out var level);
        return new ExceptionRecord(
            reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
            level, reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.GetString(6), reader.GetString(7));
    }

    private async Task UpdateStatusesAsync(long id, string telegramStatus, string emailStatus)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE exception_logs
            SET telegram_status = $telegramStatus, email_status = $emailStatus
            WHERE id = $id AND account_id = $accountId;
            """;
        command.Parameters.AddWithValue("$telegramStatus", telegramStatus);
        command.Parameters.AddWithValue("$emailStatus", emailStatus);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$accountId", _activeUserId);
        await command.ExecuteNonQueryAsync();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Every account owns an exception-monitor instance, but they write to
            // the same WAL database. Independent private connections avoid a
            // read-only/shared-cache state leaking between the management center
            // and account workspaces.
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 10
        }.ToString());
        connection.Open();
        return connection;
    }

    private static string BuildNotification(ExceptionRecord record)
    {
        var details = string.IsNullOrWhiteSpace(record.Details) ? "（无堆栈信息）" : record.Details;
        var text = $"【Telegram 控制台异常】\n编号：#{record.Id}\n时间：{record.OccurredAt:yyyy-MM-dd HH:mm:ss zzz}\n级别：{record.Level}\n来源：{record.Category}\n消息：{record.Message}\n\n{details}";
        return text.Length <= 3500 ? text : text[..3500] + "\n…内容已截断";
    }

    private static string Failure(Exception ex)
    {
        var value = $"失败：{ex.GetType().Name} - {ex.Message}";
        return value.Length <= 240 ? value : value[..240];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _logger.EntryWritten -= OnLogEntry;
        _notificationQueue.Writer.TryComplete();
        try { _notificationWorker.Wait(TimeSpan.FromSeconds(3)); } catch { }
    }
}
