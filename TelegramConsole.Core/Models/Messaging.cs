namespace TelegramConsole.Core;

public sealed record DialogItem(string Name, long Id, string Kind, bool IsGroup, bool IsForum = false)
{
    public override string ToString() => $"{(IsGroup ? "#" : "@")} {Name}";
}

public sealed record ChatLine(
    DateTime Time,
    string Chat,
    string Sender,
    string Text,
    bool IsGroup,
    long ChatId,
    bool IsOutgoing,
    bool IsMentioned,
    int MessageId = 0,
    string ChatKind = "",
    int? TopicId = null,
    int? ReplyToMessageId = null,
    string ReplySender = "",
    string ReplyText = "",
    string MediaLabel = "",
    bool HasDownloadableMedia = false)
{
    public string DisplayText => string.IsNullOrWhiteSpace(MediaLabel) || Text == MediaLabel
        ? Text
        : $"{MediaLabel} {Text}";
}
