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
    private static readonly object FileSync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TelegramConsoleApp.Settings.v1");
    private readonly EncryptedSettingsHistoryStore _history;

    public string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TelegramConsoleApp");
    public string SessionPath => Path.Combine(DataDirectory, "telegram.session");
    private string SettingsPath => Path.Combine(DataDirectory, "settings.dat");
    private string LegacySettingsPath => Path.Combine(DataDirectory, "settings.json");

    public SettingsStore()
    {
        Directory.CreateDirectory(DataDirectory);
        _history = new EncryptedSettingsHistoryStore(DataDirectory);
    }

    public string GetSessionPath(string phoneNumber)
    {
        var sessionsDirectory = Path.Combine(DataDirectory, "sessions");
        Directory.CreateDirectory(sessionsDirectory);
        return BuildAccountSessionPath(phoneNumber);
    }

    public bool IsSessionInUse(string phoneNumber)
    {
        var path = BuildAccountSessionPath(phoneNumber);
        if (!File.Exists(path)) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    public AppSettings Load()
    {
        lock (FileSync) return LoadCore();
    }

    private AppSettings LoadCore()
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
                SaveCore(migrated);
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
        lock (FileSync)
        {
            var latest = LoadCore();
            latest.ApiId = settings.ApiId;
            latest.ApiHash = settings.ApiHash;
            latest.PhoneNumber = settings.PhoneNumber;
            latest.MonitorEnabled = settings.MonitorEnabled;
            latest.ChatViewMode = settings.ChatViewMode;
            foreach (var (userId, account) in settings.Accounts)
                if (!latest.Accounts.ContainsKey(userId)) latest.Accounts[userId] = account;
            SaveCore(latest);
        }
    }

    public void SaveGlobalSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (FileSync)
        {
            var latest = LoadCore();
            latest.Language = settings.Language;
            latest.Email = settings.Email;
            latest.Proxy = settings.Proxy;
            latest.AiAssistant = settings.AiAssistant;
            SaveCore(latest);
        }
    }

    public void SaveAccount(AccountProfile account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.UserId == 0) throw new ArgumentException("Telegram 用户 ID 不能为空", nameof(account));
        lock (FileSync)
        {
            var latest = LoadCore();
            latest.Accounts[account.UserId] = account;
            SaveCore(latest);
        }
    }

    public void RemoveAccount(long userId)
    {
        lock (FileSync)
        {
            var latest = LoadCore();
            if (!latest.Accounts.Remove(userId)) return;
            SaveCore(latest);
        }
    }

    private void SaveCore(AppSettings settings)
    {
        Directory.CreateDirectory(DataDirectory);
        try
        {
            if (File.Exists(SettingsPath)) _history.Snapshot(File.ReadAllBytes(SettingsPath), "before-save");
        }
        catch
        {
            // The next primary save remains valid even if an old snapshot cannot be read.
        }
        var temp = SettingsPath + ".tmp";
        var json = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
        var encrypted = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(temp, encrypted);
        File.Move(temp, SettingsPath, true);
        _history.Snapshot(encrypted, "saved");
    }
}
