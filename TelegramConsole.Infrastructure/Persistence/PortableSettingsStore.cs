using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TelegramConsole.Core;

namespace TelegramConsole.Infrastructure;

public sealed class PortableSettingsStore : ISettingsStore
{
    private readonly object _sync = new();
    private readonly PortableDataProtection _protection;
    private readonly EncryptedSettingsHistoryStore _history;
    private readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string DataDirectory { get; }
    public string SessionPath => Path.Combine(DataDirectory, "sessions", "telegram.session");
    private string SettingsPath => Path.Combine(DataDirectory, "settings.dat");

    public PortableSettingsStore(string dataDirectory)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(Path.Combine(DataDirectory, "sessions"));
        _protection = new PortableDataProtection(DataDirectory);
        _history = new EncryptedSettingsHistoryStore(DataDirectory);
    }

    public string GetSessionPath(string phoneNumber)
    {
        var normalized = new string(phoneNumber.Where(char.IsDigit).ToArray());
        if (normalized.Length == 0) throw new ArgumentException("手机号不能为空", nameof(phoneNumber));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant()[..20];
        return Path.Combine(DataDirectory, "sessions", $"telegram-{hash}.session");
    }

    public bool IsSessionInUse(string phoneNumber)
    {
        var path = GetSessionPath(phoneNumber);
        if (!File.Exists(path)) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
    }

    public AppSettings Load()
    {
        lock (_sync)
        {
            if (!File.Exists(SettingsPath)) return new();
            var json = _protection.Unprotect(File.ReadAllBytes(SettingsPath));
            return JsonSerializer.Deserialize<AppSettings>(json, _json) ?? new();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_sync)
        {
            var latest = LoadUnsafe();
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
        lock (_sync)
        {
            var latest = LoadUnsafe();
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
        lock (_sync)
        {
            var settings = LoadUnsafe();
            settings.Accounts[account.UserId] = account;
            SaveCore(settings);
        }
    }

    public void RemoveAccount(long userId)
    {
        lock (_sync)
        {
            var settings = LoadUnsafe();
            if (settings.Accounts.Remove(userId)) SaveCore(settings);
        }
    }

    private AppSettings LoadUnsafe()
    {
        if (!File.Exists(SettingsPath)) return new();
        var json = _protection.Unprotect(File.ReadAllBytes(SettingsPath));
        return JsonSerializer.Deserialize<AppSettings>(json, _json) ?? new();
    }

    private void SaveCore(AppSettings settings)
    {
        try
        {
            if (File.Exists(SettingsPath)) _history.Snapshot(File.ReadAllBytes(SettingsPath), "before-save");
        }
        catch
        {
            // The next primary save remains valid even if an old snapshot cannot be read.
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, _json);
        var temporary = SettingsPath + ".tmp";
        var encrypted = _protection.Protect(bytes);
        File.WriteAllBytes(temporary, encrypted);
        File.Move(temporary, SettingsPath, true);
        _history.Snapshot(encrypted, "saved");
    }
}
