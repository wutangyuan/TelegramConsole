namespace TelegramConsole.Core;

public sealed record DialogItem(string Name, long Id, string Kind, bool IsGroup)
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
    bool IsMentioned);
