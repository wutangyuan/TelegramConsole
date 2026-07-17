namespace TelegramConsole.Core;

public sealed record DialogItem(string Name, long Id, string Kind, bool IsGroup, bool IsForum = false)
{
    public override string ToString() => $"{(IsGroup ? "#" : "@")} {Name}";
}

public enum ChatMediaKind
{
    None,
    Photo,
    Video,
    Animation,
    Audio,
    Voice,
    VideoNote,
    Document,
    Sticker,
    Poll,
    Location,
    Contact,
    Game,
    WebPage,
    Other
}

public sealed record ChatMediaInfo(
    ChatMediaKind Kind,
    string Label,
    string FileName = "",
    string MimeType = "",
    long Size = 0,
    int DurationSeconds = 0,
    bool IsDownloadable = false);

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
    bool HasDownloadableMedia = false,
    ChatMediaInfo? Media = null)
{
    public string DisplayText => string.IsNullOrWhiteSpace(MediaLabel) || Text == MediaLabel
        ? Text
        : $"{MediaLabel} {Text}";
}

public sealed record DeletedMessageInfo(int MessageId, string Sender, string Text);

public sealed record MessageDeletion(
    DateTime Time,
    long ChatId,
    string ChatKind,
    string Chat,
    IReadOnlyList<DeletedMessageInfo> Messages)
{
    public IReadOnlyList<int> MessageIds => Messages.Select(x => x.MessageId).ToArray();
}
