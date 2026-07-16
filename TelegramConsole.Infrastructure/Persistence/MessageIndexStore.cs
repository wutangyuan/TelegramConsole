using Microsoft.Data.Sqlite;
using TelegramConsole.Core;

namespace TelegramConsole.Infrastructure;

internal sealed class MessageIndexStore
{
    private readonly string _databasePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public MessageIndexStore(ISettingsStore store) =>
        _databasePath = Path.Combine(store.DataDirectory, "messages.db");

    public string DatabasePath => _databasePath;

    public async Task IndexAsync(long accountId, IEnumerable<MessageSearchResult> messages)
    {
        if (accountId == 0) return;
        await EnsureInitializedAsync();
        await _gate.WaitAsync();
        try
        {
            await using var connection = OpenConnection();
            await using var transaction = connection.BeginTransaction();
            foreach (var item in messages.Where(x => x.MessageId != 0))
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO message_index(account_id, chat_id, chat_kind, chat_title, message_id,
                                              occurred_at, sender, content, is_outgoing, topic_id)
                    VALUES($account, $chat, $kind, $title, $message, $at, $sender, $content, $outgoing, $topic)
                    ON CONFLICT(account_id, chat_id, message_id) DO UPDATE SET
                        chat_kind=excluded.chat_kind, chat_title=excluded.chat_title,
                        occurred_at=excluded.occurred_at, sender=excluded.sender,
                        content=excluded.content, is_outgoing=excluded.is_outgoing, topic_id=excluded.topic_id;
                    """;
                command.Parameters.AddWithValue("$account", accountId);
                command.Parameters.AddWithValue("$chat", item.ChatId);
                command.Parameters.AddWithValue("$kind", item.ChatKind);
                command.Parameters.AddWithValue("$title", item.ChatTitle);
                command.Parameters.AddWithValue("$message", item.MessageId);
                command.Parameters.AddWithValue("$at", item.Time.ToUniversalTime().ToString("O"));
                command.Parameters.AddWithValue("$sender", item.Sender);
                command.Parameters.AddWithValue("$content", item.Text);
                command.Parameters.AddWithValue("$outgoing", item.IsOutgoing ? 1 : 0);
                command.Parameters.AddWithValue("$topic", (object?)item.TopicId ?? DBNull.Value);
                await command.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<MessageSearchResult>> SearchAsync(
        long accountId, string query, DialogItem? dialog, int limit)
    {
        await EnsureInitializedAsync();
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => $"\"{x.Replace("\"", "\"\"")}\"");
        var match = string.Join(" AND ", terms);
        if (match.Length == 0) return [];
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT chat_id, chat_kind, chat_title, message_id, occurred_at, sender, content,
                   is_outgoing, topic_id
            FROM (
                SELECT m.chat_id, m.chat_kind, m.chat_title, m.message_id, m.occurred_at,
                       m.sender, m.content, m.is_outgoing, m.topic_id
                FROM message_index AS m
                JOIN message_index_fts AS f ON f.rowid=m.id
                WHERE m.account_id=$account AND message_index_fts MATCH $query
                  AND ($chat=0 OR m.chat_id=$chat)
                UNION
                SELECT m.chat_id, m.chat_kind, m.chat_title, m.message_id, m.occurred_at,
                       m.sender, m.content, m.is_outgoing, m.topic_id
                FROM message_index AS m
                WHERE m.account_id=$account AND ($chat=0 OR m.chat_id=$chat)
                  AND (m.content LIKE $like ESCAPE '\' OR m.sender LIKE $like ESCAPE '\'
                       OR m.chat_title LIKE $like ESCAPE '\')
            )
            ORDER BY occurred_at DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$query", match);
        command.Parameters.AddWithValue("$like", $"%{EscapeLike(query)}%");
        command.Parameters.AddWithValue("$chat", dialog?.Id ?? 0);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        var result = new List<MessageSearchResult>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
                DateTimeOffset.Parse(reader.GetString(4)).LocalDateTime, reader.GetString(5), reader.GetString(6),
                reader.GetInt32(7) != 0, reader.IsDBNull(8) ? null : reader.GetInt32(8), "本地索引"));
        return result;
    }

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\")
        .Replace("%", "\\%")
        .Replace("_", "\\_");

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        await _gate.WaitAsync();
        try
        {
            if (_initialized) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS message_index(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    account_id INTEGER NOT NULL, chat_id INTEGER NOT NULL, chat_kind TEXT NOT NULL,
                    chat_title TEXT NOT NULL, message_id INTEGER NOT NULL, occurred_at TEXT NOT NULL,
                    sender TEXT NOT NULL, content TEXT NOT NULL, is_outgoing INTEGER NOT NULL,
                    topic_id INTEGER NULL);
                CREATE UNIQUE INDEX IF NOT EXISTS ux_message_index_identity
                    ON message_index(account_id, chat_id, message_id);
                CREATE VIRTUAL TABLE IF NOT EXISTS message_index_fts USING fts5(
                    chat_title, sender, content, content='message_index', content_rowid='id',
                    tokenize='unicode61');
                CREATE TRIGGER IF NOT EXISTS message_index_ai AFTER INSERT ON message_index BEGIN
                    INSERT INTO message_index_fts(rowid, chat_title, sender, content)
                    VALUES(new.id, new.chat_title, new.sender, new.content);
                END;
                CREATE TRIGGER IF NOT EXISTS message_index_ad AFTER DELETE ON message_index BEGIN
                    INSERT INTO message_index_fts(message_index_fts, rowid, chat_title, sender, content)
                    VALUES('delete', old.id, old.chat_title, old.sender, old.content);
                END;
                CREATE TRIGGER IF NOT EXISTS message_index_au AFTER UPDATE ON message_index BEGIN
                    INSERT INTO message_index_fts(message_index_fts, rowid, chat_title, sender, content)
                    VALUES('delete', old.id, old.chat_title, old.sender, old.content);
                    INSERT INTO message_index_fts(rowid, chat_title, sender, content)
                    VALUES(new.id, new.chat_title, new.sender, new.content);
                END;
                """;
            await command.ExecuteNonQueryAsync();
            _initialized = true;
        }
        finally { _gate.Release(); }
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
