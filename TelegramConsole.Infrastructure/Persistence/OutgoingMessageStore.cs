using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TelegramConsole.Core;

namespace TelegramConsole.Infrastructure;

internal sealed class OutgoingMessageStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TelegramConsoleApp.Outbox.v1");
    private readonly string _databasePath;

    public string DatabasePath => _databasePath;

    public OutgoingMessageStore(ISettingsStore store)
    {
        _databasePath = Path.Combine(store.DataDirectory, "outbox.db");
        Initialize();
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS outgoing_messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                account_id INTEGER NOT NULL,
                idempotency_key TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                target_id INTEGER NOT NULL,
                target_kind TEXT NOT NULL,
                target_title TEXT NOT NULL,
                purpose TEXT NOT NULL,
                encrypted_payload BLOB NOT NULL,
                status TEXT NOT NULL,
                attempt_count INTEGER NOT NULL DEFAULT 0,
                telegram_message_id INTEGER NULL,
                error TEXT NOT NULL DEFAULT '',
                UNIQUE(account_id, idempotency_key)
            );
            CREATE INDEX IF NOT EXISTS ix_outgoing_account_created
                ON outgoing_messages(account_id, created_at DESC);
            UPDATE outgoing_messages
               SET status = 'Unknown',
                   updated_at = CURRENT_TIMESTAMP,
                   error = CASE WHEN error = '' THEN '应用上次退出时发送尚未确认' ELSE error END
             WHERE status IN ('Queued', 'Sending');
            """;
        command.ExecuteNonQuery();
    }

    public async Task<OutgoingEnvelope> GetOrCreateAsync(
        long accountId,
        string idempotencyKey,
        long targetId,
        string targetKind,
        string targetTitle,
        string purpose,
        string message)
    {
        await using var connection = OpenConnection();
        var now = DateTimeOffset.Now;
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT OR IGNORE INTO outgoing_messages
                    (account_id, idempotency_key, created_at, updated_at, target_id, target_kind,
                     target_title, purpose, encrypted_payload, status)
                VALUES
                    ($account, $key, $created, $updated, $target, $kind,
                     $title, $purpose, $payload, 'Queued');
                """;
            insert.Parameters.AddWithValue("$account", accountId);
            insert.Parameters.AddWithValue("$key", idempotencyKey);
            insert.Parameters.AddWithValue("$created", now.ToString("O"));
            insert.Parameters.AddWithValue("$updated", now.ToString("O"));
            insert.Parameters.AddWithValue("$target", targetId);
            insert.Parameters.AddWithValue("$kind", targetKind);
            insert.Parameters.AddWithValue("$title", targetTitle);
            insert.Parameters.AddWithValue("$purpose", purpose);
            insert.Parameters.Add("$payload", SqliteType.Blob).Value = Protect(message);
            await insert.ExecuteNonQueryAsync();
        }
        return await GetByKeyAsync(connection, accountId, idempotencyKey)
               ?? throw new InvalidOperationException("无法创建发件箱记录");
    }

    public async Task<OutgoingEnvelope?> GetAsync(long accountId, long id)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM outgoing_messages WHERE account_id=$account AND id=$id;";
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadEnvelope(reader) : null;
    }

    public async Task<OutgoingEnvelope?> GetByKeyAsync(long accountId, string idempotencyKey)
    {
        await using var connection = OpenConnection();
        return await GetByKeyAsync(connection, accountId, idempotencyKey);
    }

    private static async Task<OutgoingEnvelope?> GetByKeyAsync(
        SqliteConnection connection,
        long accountId,
        string idempotencyKey)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM outgoing_messages WHERE account_id=$account AND idempotency_key=$key;";
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadEnvelope(reader) : null;
    }

    public async Task UpdateAsync(
        long id,
        OutgoingMessageStatus status,
        int attemptCount,
        int? telegramMessageId = null,
        string error = "")
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE outgoing_messages
               SET status=$status, updated_at=$updated, attempt_count=$attempt,
                   telegram_message_id=$messageId, error=$error
             WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$updated", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$attempt", attemptCount);
        command.Parameters.AddWithValue("$messageId", telegramMessageId is null ? DBNull.Value : telegramMessageId.Value);
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<OutgoingMessageRecord>> QueryAsync(long accountId, int limit)
    {
        var result = new List<OutgoingMessageRecord>();
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM outgoing_messages
             WHERE account_id=$account
             ORDER BY id DESC
             LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 2000));
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(ToRecord(ReadEnvelope(reader)));
        return result;
    }

    public async Task<int> DeleteSentBeforeAsync(DateTimeOffset cutoff)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM outgoing_messages
             WHERE status=$status
               AND julianday(updated_at) < julianday($cutoff);
            """;
        command.Parameters.AddWithValue("$status", OutgoingMessageStatus.Sent.ToString());
        command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
        return await command.ExecuteNonQueryAsync();
    }

    private static OutgoingEnvelope ReadEnvelope(SqliteDataReader reader) => new(
        reader.GetInt64(reader.GetOrdinal("id")),
        reader.GetInt64(reader.GetOrdinal("account_id")),
        reader.GetString(reader.GetOrdinal("idempotency_key")),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at"))),
        reader.GetInt64(reader.GetOrdinal("target_id")),
        reader.GetString(reader.GetOrdinal("target_kind")),
        reader.GetString(reader.GetOrdinal("target_title")),
        reader.GetString(reader.GetOrdinal("purpose")),
        Unprotect((byte[])reader["encrypted_payload"]),
        Enum.TryParse<OutgoingMessageStatus>(reader.GetString(reader.GetOrdinal("status")), out var status)
            ? status : OutgoingMessageStatus.Unknown,
        reader.GetInt32(reader.GetOrdinal("attempt_count")),
        reader.IsDBNull(reader.GetOrdinal("telegram_message_id"))
            ? null : reader.GetInt32(reader.GetOrdinal("telegram_message_id")),
        reader.GetString(reader.GetOrdinal("error")));

    private static OutgoingMessageRecord ToRecord(OutgoingEnvelope value) => new(
        value.Id, value.CreatedAt, value.UpdatedAt, value.TargetId, value.TargetKind,
        value.TargetTitle, value.Purpose, Preview(value.Message), value.Status,
        value.AttemptCount, value.TelegramMessageId, value.Error);

    private static string Preview(string message)
    {
        var normalized = message.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 120 ? normalized : normalized[..120] + "…";
    }

    private static byte[] Protect(string value)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("发件箱正文加密需要 Windows DPAPI");
        return ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
    }

    private static string Unprotect(byte[] value)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("发件箱正文解密需要 Windows DPAPI");
        return Encoding.UTF8.GetString(
            ProtectedData.Unprotect(value, Entropy, DataProtectionScope.CurrentUser));
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        connection.Open();
        return connection;
    }
}

internal sealed record OutgoingEnvelope(
    long Id,
    long AccountId,
    string IdempotencyKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long TargetId,
    string TargetKind,
    string TargetTitle,
    string Purpose,
    string Message,
    OutgoingMessageStatus Status,
    int AttemptCount,
    int? TelegramMessageId,
    string Error);
