namespace TelegramConsole.Core;

public sealed record MessageSearchResult(
    long ChatId,
    string ChatKind,
    string ChatTitle,
    int MessageId,
    DateTime Time,
    string Sender,
    string Text,
    bool IsOutgoing,
    int? TopicId = null,
    string Source = "Telegram");

public sealed record ForumTopicItem(int Id, string Title, int UnreadCount);

public sealed record ServerScheduledMessage(
    long ChatId,
    string ChatKind,
    string ChatTitle,
    int MessageId,
    DateTime SendAt,
    string Text);

public sealed record DialogFolderItem(int Id, string Title, int DialogCount);

public enum AutomationTrigger
{
    Keyword,
    RegularExpression,
    Mention,
    Chat,
    Sender
}

public enum AutomationAction
{
    Telegram,
    Email,
    LogOnly
}

public sealed class AutomationRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "新规则";
    public AutomationTrigger Trigger { get; set; } = AutomationTrigger.Keyword;
    public string Pattern { get; set; } = "";
    public long? ChatId { get; set; }
    public AutomationAction Action { get; set; } = AutomationAction.LogOnly;
    public long? TargetPeerId { get; set; }
    public string TargetPeerKind { get; set; } = "";
    public string TargetPeerTitle { get; set; } = "";
    public string EmailRecipient { get; set; } = "";
    public string MessageTemplate { get; set; } = "规则 {规则} 命中：[{群聊}] {发送人}: {内容}";
}
