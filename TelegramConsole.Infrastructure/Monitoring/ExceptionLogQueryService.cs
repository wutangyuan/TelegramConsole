using Microsoft.Data.Sqlite;
using TelegramConsole.Core;

namespace TelegramConsole.Infrastructure;

public sealed class ExceptionLogQueryService : IExceptionLogQueryService
{
    public string DatabasePath { get; }

    public ExceptionLogQueryService(ISettingsStore store) =>
        DatabasePath = Path.Combine(store.DataDirectory, "exceptions.db");

    public async Task<IReadOnlyList<GlobalExceptionRecord>> GetRecentAsync(int limit = 200)
    {
        if (!File.Exists(DatabasePath)) return [];
        var result = new List<GlobalExceptionRecord>();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            // A read-only connection must never join the shared cache used by the
            // per-account writers. If it opens first, SQLite can otherwise mark
            // that shared cache read-only for the whole process.
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, account_id, occurred_at, level, category, message, details,
                   telegram_status, email_status
              FROM exception_logs
             ORDER BY id DESC
             LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 2000));
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new GlobalExceptionRecord(
                reader.GetInt64(0),
                reader.GetInt64(1),
                DateTimeOffset.Parse(reader.GetString(2)),
                Enum.TryParse<AppLogLevel>(reader.GetString(3), out var level) ? level : AppLogLevel.Error,
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8)));
        }
        return result;
    }
}
