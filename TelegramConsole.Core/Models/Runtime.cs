namespace TelegramConsole.Core;

public sealed class ManagedAccountDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string LocalName { get; set; } = "";
    public int ApiId { get; set; }
    public string ApiHash { get; set; } = "";
    public string PhoneNumber { get; set; } = "";
    public bool AutoStart { get; set; } = true;
    public long TelegramUserId { get; set; }
    public string TelegramDisplayName { get; set; } = "";
    public ProxySettings Proxy { get; set; } = new();
}

public enum AccountRuntimeStatus
{
    Stopped,
    Starting,
    AwaitingLoginInput,
    Online,
    Recovering,
    Faulted
}

public sealed record AccountRuntimeSnapshot(
    Guid Id,
    string LocalName,
    string PhoneNumberMasked,
    long TelegramUserId,
    string TelegramDisplayName,
    bool AutoStart,
    AccountRuntimeStatus Status,
    string StatusMessage,
    string LoginPrompt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastActivityAt);

public sealed record CreateManagedAccountRequest(
    string LocalName,
    int ApiId,
    string ApiHash,
    string PhoneNumber,
    bool AutoStart,
    ProxySettings? Proxy = null);

public sealed record SendChatMessageRequest(long DialogId, string DialogKind, string DialogName, string Message);

public sealed record ApplicationResourceSnapshot(
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    long ManagedHeapBytes,
    ulong DiskReadBytes,
    ulong DiskWriteBytes,
    double DiskReadBytesPerSecond,
    double DiskWriteBytesPerSecond,
    long UploadedBytes,
    long DownloadedBytes,
    double UploadBytesPerSecond,
    double DownloadBytesPerSecond,
    long DataDirectoryBytes,
    long MediaCacheBytes,
    TimeSpan Uptime,
    DateTimeOffset CapturedAt);
