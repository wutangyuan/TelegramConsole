using System.Text.Json;
using TelegramConsole.Core;

namespace TelegramConsole.Infrastructure;

public sealed class EncryptedManagedAccountCatalog : IManagedAccountCatalog
{
    private readonly object _sync = new();
    private readonly string _path;
    private readonly PortableDataProtection _protection;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public EncryptedManagedAccountCatalog(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "accounts.dat");
        _protection = new PortableDataProtection(dataDirectory);
    }

    public IReadOnlyList<ManagedAccountDefinition> Load()
    {
        lock (_sync) return LoadUnsafe().Select(Clone).ToArray();
    }

    public void Save(ManagedAccountDefinition account)
    {
        lock (_sync)
        {
            var accounts = LoadUnsafe();
            var index = accounts.FindIndex(x => x.Id == account.Id);
            if (index < 0) accounts.Add(Clone(account)); else accounts[index] = Clone(account);
            Write(accounts);
        }
    }

    public void Remove(Guid accountId)
    {
        lock (_sync)
        {
            var accounts = LoadUnsafe();
            if (accounts.RemoveAll(x => x.Id == accountId) > 0) Write(accounts);
        }
    }

    private List<ManagedAccountDefinition> LoadUnsafe()
    {
        if (!File.Exists(_path)) return [];
        var bytes = _protection.Unprotect(File.ReadAllBytes(_path));
        return JsonSerializer.Deserialize<List<ManagedAccountDefinition>>(bytes, _json) ?? [];
    }

    private void Write(List<ManagedAccountDefinition> accounts)
    {
        var temporary = _path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(accounts, _json);
        File.WriteAllBytes(temporary, _protection.Protect(bytes));
        File.Move(temporary, _path, true);
    }

    private static ManagedAccountDefinition Clone(ManagedAccountDefinition value) => new()
    {
        Id = value.Id,
        LocalName = value.LocalName,
        ApiId = value.ApiId,
        ApiHash = value.ApiHash,
        PhoneNumber = value.PhoneNumber,
        AutoStart = value.AutoStart,
        TelegramUserId = value.TelegramUserId,
        TelegramDisplayName = value.TelegramDisplayName,
        Proxy = new ProxySettings
        {
            Enabled = value.Proxy.Enabled,
            Type = value.Proxy.Type,
            Host = value.Proxy.Host,
            Port = value.Proxy.Port,
            UserName = value.Proxy.UserName,
            Password = value.Proxy.Password,
            MtProxyUrl = value.Proxy.MtProxyUrl
        }
    };
}
