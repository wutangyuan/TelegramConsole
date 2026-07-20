using Microsoft.Data.Sqlite;

namespace TelegramConsole.Infrastructure;

/// <summary>
/// Keeps encrypted settings snapshots for recovery. Payloads are already protected by the
/// owning settings store (DPAPI on Windows, AES-GCM in portable deployments), so the database
/// never contains plaintext credentials.
/// </summary>
internal sealed class EncryptedSettingsHistoryStore
{
    private const int RetentionCount = 20;
    private readonly string _databasePath;

    public EncryptedSettingsHistoryStore(string dataDirectory) =>
        _databasePath = Path.Combine(dataDirectory, "settings-history.db");

    public void Snapshot(byte[] encryptedPayload, string reason)
    {
        if (encryptedPayload.Length == 0) return;
        try
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath};Mode=ReadWriteCreate;Cache=Shared");
            connection.Open();
            using (var create = connection.CreateCommand())
            {
                create.CommandText = """
                    CREATE TABLE IF NOT EXISTS settings_snapshots (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        saved_at TEXT NOT NULL,
                        reason TEXT NOT NULL,
                        encrypted_payload BLOB NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS ix_settings_snapshots_saved_at
                        ON settings_snapshots(saved_at DESC);
                    """;
                create.ExecuteNonQuery();
            }

            using (var insert = connection.CreateCommand())
            {
                insert.CommandText = """
                    INSERT INTO settings_snapshots(saved_at, reason, encrypted_payload)
                    VALUES($savedAt, $reason, $payload);
                    """;
                insert.Parameters.AddWithValue("$savedAt", DateTimeOffset.UtcNow.ToString("O"));
                insert.Parameters.AddWithValue("$reason", reason);
                insert.Parameters.Add("$payload", SqliteType.Blob).Value = encryptedPayload;
                insert.ExecuteNonQuery();
            }

            using var cleanup = connection.CreateCommand();
            cleanup.CommandText = """
                DELETE FROM settings_snapshots
                WHERE id NOT IN (
                    SELECT id FROM settings_snapshots ORDER BY id DESC LIMIT $retention
                );
                """;
            cleanup.Parameters.AddWithValue("$retention", RetentionCount);
            cleanup.ExecuteNonQuery();
        }
        catch
        {
            // A recovery snapshot must never prevent the primary encrypted settings save.
        }
    }
}
