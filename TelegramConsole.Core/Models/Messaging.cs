namespace TelegramConsole.Core;

public enum DialogCategory
{
    Unknown,
    Private,
    Bot,
    Group,
    Supergroup,
    Channel
}

public sealed record DialogItem(
    string Name,
    long Id,
    string Kind,
    bool IsGroup,
    bool IsForum = false,
    DialogCategory Category = DialogCategory.Unknown)
{
    public DialogCategory EffectiveCategory => Category != DialogCategory.Unknown
        ? Category
        : Kind switch
        {
            "Chat" => DialogCategory.Group,
            "Channel" => DialogCategory.Channel,
            _ => DialogCategory.Private
        };

    public bool IsBot => EffectiveCategory == DialogCategory.Bot;

    public string CategoryIcon => EffectiveCategory switch
    {
        DialogCategory.Bot => "🤖",
        DialogCategory.Group => "👥",
        DialogCategory.Supergroup => "👥",
        DialogCategory.Channel => "📢",
        _ => "👤"
    };

    public string DisplayName => $"{CategoryIcon} {Name}";

    public override string ToString() => DisplayName;
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
    bool IsDownloadable = false,
    string Title = "",
    string Description = "");

public sealed record ChatReaction(string Symbol, int Count, bool IsChosen = false);

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
    ChatMediaInfo? Media = null,
    bool IsEdited = false,
    bool IsPinned = false,
    string ForwardedFrom = "",
    string AuthorSignature = "",
    int ViewCount = 0,
    int ForwardCount = 0,
    long GroupedId = 0,
    IReadOnlyList<ChatReaction>? Reactions = null,
    bool IsDeleted = false)
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
