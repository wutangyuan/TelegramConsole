using System.Globalization;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using TelegramConsole.Core;

namespace TelegramConsole.Infrastructure;

public sealed class MentionMonitorService : IMentionMonitorService
{
    private readonly ITelegramService _telegram;
    private readonly IAppLogger _logger;
    private readonly Channel<long> _notificationQueue = Channel.CreateUnbounded<long>();
    private readonly Task _notificationWorker;
    private long _activeUserId;
    private MentionAlertSettings? _activeSettings;
    private bool _disposed;

    public string DatabasePath { get; }
    public event Action? RecordsChanged;

    public MentionMonitorService(ISettingsStore store, ITelegramService telegram, IAppLogger logger)
    {
        _telegram = telegram;
        _logger = logger;
        DatabasePath = Path.Combine(store.DataDirectory, "mentions.db");
        InitializeDatabase();
        _telegram.MessageReceived += OnMessageReceived;
        _notificationWorker = Task.Run(ProcessNotificationsAsync);
        _logger.Info("MentionMonitor", "@我的消息监控已启动");
    }

    public void ActivateAccount(long userId, MentionAlertSettings settings)
    {
        _activeUserId = userId;
        _activeSettings = settings;
        RecordsChanged?.Invoke();
    }

    public void DeactivateAccount()
    {
        _activeUserId = 0;
        _activeSettings = null;
        RecordsChanged?.Invoke();
    }

    private void OnMessageReceived(ChatLine line)
    {
        if (_disposed || _activeUserId == 0 || !line.IsGroup || line.IsOutgoing || !line.IsMentioned) return;
        try
        {
            var status = _activeSettings?.NotificationsEnabled == true &&
                         _activeSettings.TargetPeerId is not null
                ? "待发送"
                : "未配置";
            var id = Insert(line, status);
            RecordsChanged?.Invoke();
            if (status == "待发送") _notificationQueue.Writer.TryWrite(id);
        }
        catch (Exception ex)
        {
            _logger.Error("MentionMonitor", "@消息入库失败", ex);
        }
    }

    private void InitializeDatabase()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS mention_messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                account_id INTEGER NOT NULL,
                occurred_at TEXT NOT NULL,
                chat_id INTEGER NOT NULL,
                chat_name TEXT NOT NULL,
                sender TEXT NOT NULL,
                message TEXT NOT NULL,
                notification_status TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_mention_messages_account_time
                ON mention_messages(account_id, occurred_at DESC);
            """;
        command.ExecuteNonQuery();
    }

    private long Insert(ChatLine line, string status)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mention_messages
                (account_id, occurred_at, chat_id, chat_name, sender, message, notification_status)
            VALUES
                ($accountId, $occurredAt, $chatId, $chatName, $sender, $message, $status);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$accountId", _activeUserId);
        command.Parameters.AddWithValue("$occurredAt", new DateTimeOffset(line.Time).ToString("O"));
        command.Parameters.AddWithValue("$chatId", line.ChatId);
        command.Parameters.AddWithValue("$chatName", line.Chat);
        command.Parameters.AddWithValue("$sender", line.Sender);
        command.Parameters.AddWithValue("$message", line.Text);
        command.Parameters.AddWithValue("$status", status);
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public async Task<IReadOnlyList<MentionRecord>> QueryAsync(MentionQuery query)
    {
        if (_activeUserId == 0) return [];
        var records = new List<MentionRecord>();
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        var keywordCondition = string.IsNullOrWhiteSpace(query.Keyword)
            ? ""
            : "AND (chat_name LIKE $keyword OR sender LIKE $keyword OR message LIKE $keyword)";
        command.CommandText = $"""
            SELECT id, occurred_at, chat_name, sender, message, notification_status
            FROM mention_messages
            WHERE account_id = $accountId {keywordCondition}
            ORDER BY id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$accountId", _activeUserId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 2000));
        if (keywordCondition.Length > 0)
            command.Parameters.AddWithValue("$keyword", $"%{query.Keyword.Trim()}%");
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            records.Add(new MentionRecord(
                reader.GetInt64(0),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        return records;
    }

    public async Task SendTestNotificationAsync()
    {
        var settings = _activeSettings ?? throw new InvalidOperationException("请先登录 Telegram 账号");
        if (!settings.NotificationsEnabled || settings.TargetPeerId is not long targetId)
            throw new InvalidOperationException("请先启用通知并选择机器人或私聊");
        await _telegram.SendAsync(
            new DialogItem(settings.TargetPeerTitle, targetId, settings.TargetPeerKind, false),
            $"【@我的消息通知测试】\n账号：{_telegram.CurrentUser}\n时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    }

    private async Task ProcessNotificationsAsync()
    {
        await foreach (var id in _notificationQueue.Reader.ReadAllAsync())
            await SendNotificationAsync(id);
    }

    private async Task SendNotificationAsync(long id)
    {
        var settings = _activeSettings;
        if (_activeUserId == 0 || settings?.TargetPeerId is not long targetId) return;
        var record = await GetByIdAsync(id);
        if (record is null) return;
        try
        {
            var text = $"【有人 @ 我】\n群聊：{record.ChatName}\n发送人：{record.Sender}\n时间：{record.OccurredAt:yyyy-MM-dd HH:mm:ss}\n\n{record.Message}";
            if (text.Length > 3500) text = text[..3500] + "\n…内容已截断";
            await _telegram.SendAsync(
                new DialogItem(settings.TargetPeerTitle, targetId, settings.TargetPeerKind, false), text);
            await UpdateStatusAsync(id, "已发送");
        }
        catch (Exception ex)
        {
            await UpdateStatusAsync(id, $"失败：{ex.Message}"[..Math.Min(200, $"失败：{ex.Message}".Length)]);
            _logger.Warning("MentionMonitor", $"@消息通知发送失败，记录 #{id}", ex);
        }
        RecordsChanged?.Invoke();
    }

    private async Task<MentionRecord?> GetByIdAsync(long id)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, occurred_at, chat_name, sender, message, notification_status
            FROM mention_messages WHERE id = $id AND account_id = $accountId;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$accountId", _activeUserId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new MentionRecord(
            reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
            reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5));
    }

    private async Task UpdateStatusAsync(long id, string status)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE mention_messages SET notification_status = $status
            WHERE id = $id AND account_id = $accountId;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$accountId", _activeUserId);
        await command.ExecuteNonQueryAsync();
    }

    private SqliteConnection OpenConnection()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        connection.Open();
        return connection;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _telegram.MessageReceived -= OnMessageReceived;
        _notificationQueue.Writer.TryComplete();
        try { _notificationWorker.Wait(TimeSpan.FromSeconds(3)); } catch { }
    }
}
