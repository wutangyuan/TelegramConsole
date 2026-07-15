using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.Versioning;
using TelegramConsole.Core;

namespace TelegramConsole.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed class SettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TelegramConsoleApp.Settings.v1");

    public string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TelegramConsoleApp");
    public string SessionPath => Path.Combine(DataDirectory, "telegram.session");
    private string SettingsPath => Path.Combine(DataDirectory, "settings.dat");
    private string LegacySettingsPath => Path.Combine(DataDirectory, "settings.json");

    public SettingsStore() => Directory.CreateDirectory(DataDirectory);

    public string GetSessionPath(string phoneNumber)
    {
        var sessionsDirectory = Path.Combine(DataDirectory, "sessions");
        Directory.CreateDirectory(sessionsDirectory);
        return BuildAccountSessionPath(phoneNumber);
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var encrypted = File.ReadAllBytes(SettingsPath);
                var json = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                return Prepare(JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new());
            }
            if (File.Exists(LegacySettingsPath))
            {
                var migrated = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(LegacySettingsPath), JsonOptions) ?? new();
                Save(migrated);
                File.Delete(LegacySettingsPath);
                return Prepare(migrated);
            }
            return new();
        }
        catch
        {
            return new();
        }
    }

    private AppSettings Prepare(AppSettings settings)
    {
        // Bind the legacy single session to the last saved phone before the UI can change it.
        if (!string.IsNullOrWhiteSpace(settings.PhoneNumber) && File.Exists(SessionPath))
        {
            var accountSessionPath = BuildAccountSessionPath(settings.PhoneNumber);
            Directory.CreateDirectory(Path.GetDirectoryName(accountSessionPath)!);
            if (!File.Exists(accountSessionPath)) File.Copy(SessionPath, accountSessionPath);
        }
        return settings;
    }

    private string BuildAccountSessionPath(string phoneNumber)
    {
        var normalized = new string(phoneNumber.Where(char.IsDigit).ToArray());
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant()[..20];
        return Path.Combine(DataDirectory, "sessions", $"telegram-{hash}.session");
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DataDirectory);
        var temp = SettingsPath + ".tmp";
        var json = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
        var encrypted = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(temp, encrypted);
        File.Move(temp, SettingsPath, true);
    }
}
