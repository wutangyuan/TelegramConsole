namespace TelegramConsole.Core;

public sealed class AppSettings
{
    public string Language { get; set; } = "";
    public int ApiId { get; set; }
    public string ApiHash { get; set; } = "";
    public string PhoneNumber { get; set; } = "";
    public bool MonitorEnabled { get; set; } = true;
    public List<ScheduledMessage> Schedules { get; set; } = [];
    public EmailSettings Email { get; set; } = new();
    public ProxySettings Proxy { get; set; } = new();
    public ExceptionAlertSettings ExceptionAlerts { get; set; } = new();
    public Dictionary<long, AccountProfile> Accounts { get; set; } = [];
}

public sealed class AccountProfile
{
    public long UserId { get; set; }
    public string LocalName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PhoneNumber { get; set; } = "";
    public bool AutoStart { get; set; } = true;
    public int SortOrder { get; set; }
    public List<ScheduledMessage> Schedules { get; set; } = [];
    public ExceptionAlertSettings ExceptionAlerts { get; set; } = new();
    public MentionAlertSettings MentionAlerts { get; set; } = new();
    public List<AutomationRule> AutomationRules { get; set; } = [];
    public List<IntervalChatRule> IntervalChatRules { get; set; } = [];
}

public sealed class IntervalChatRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "聊天简报";
    public long SourceChatId { get; set; }
    public string SourceChatKind { get; set; } = "Channel";
    public string SourceChatTitle { get; set; } = "";
    public long TargetChatId { get; set; }
    public string TargetChatKind { get; set; } = "Channel";
    public string TargetChatTitle { get; set; } = "";
    public int IntervalMinutes { get; set; } = 3;
    public int MinimumMessageCount { get; set; } = 5;
    public int SummaryLineCount { get; set; } = 8;
    public DateTimeOffset? WindowStartedAt { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public DateTimeOffset? LastSentAt { get; set; }
    public int LastObservedMessageCount { get; set; }
    public string LastStatus { get; set; } = "等待首次检查";
}

public sealed class MentionAlertSettings
{
    public bool NotificationsEnabled { get; set; }
    public long? TargetPeerId { get; set; }
    public string TargetPeerKind { get; set; } = "";
    public string TargetPeerTitle { get; set; } = "";
}

public sealed class ExceptionAlertSettings
{
    public bool NotificationsEnabled { get; set; } = true;
    public AppLogLevel MinimumLevel { get; set; } = AppLogLevel.Error;
    public long? TelegramPeerId { get; set; }
    public string TelegramPeerKind { get; set; } = "";
    public string TelegramPeerTitle { get; set; } = "";
    public string EmailRecipient { get; set; } = "";
}

public sealed class ScheduledMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public long ChatId { get; set; }
    public string ChatKind { get; set; } = "Channel";
    public string ChatTitle { get; set; } = "";
    public TimeSpan Time { get; set; } = new(8, 0, 0);
    public SchedulePeriod Period { get; set; } = SchedulePeriod.Daily;
    public List<DayOfWeek> WeekDays { get; set; } = [];
    public string Message { get; set; } = "今日签到";
    public DateOnly? LastSentDate { get; set; }
    public long? ConfirmationPeerId { get; set; }
    public string ConfirmationPeerKind { get; set; } = "";
    public string ConfirmationPeerTitle { get; set; } = "";
    public string ConfirmationEmail { get; set; } = "";
    public string ConfirmationText { get; set; } = "签到完成：{群聊}，时间 {时间}";
}

public enum SchedulePeriod
{
    Daily,
    Weekly
}

public sealed class EmailSettings
{
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromAddress { get; set; } = "";
}

public sealed class ProxySettings
{
    public bool Enabled { get; set; } = true;
    public string Type { get; set; } = "Socks5";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7890;
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public string MtProxyUrl { get; set; } = "";
}
