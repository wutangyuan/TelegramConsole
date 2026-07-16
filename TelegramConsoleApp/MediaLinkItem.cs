namespace TelegramConsoleApp;

internal sealed record MediaLinkItem(DialogItem Dialog, int MessageId, string Label);

internal static class MediaLinkFactory
{
    public static IReadOnlyList<TerminalInlineLink> Create(
        ChatLine line,
        string renderedLine,
        int lineIndex)
    {
        if (!line.HasDownloadableMedia || string.IsNullOrWhiteSpace(line.MediaLabel)) return [];
        var start = renderedLine.IndexOf(line.MediaLabel, StringComparison.Ordinal);
        if (start < 0) return [];
        var dialog = new DialogItem(line.Chat, line.ChatId, line.ChatKind, line.IsGroup);
        return
        [
            new TerminalInlineLink(
                lineIndex, start, line.MediaLabel.Length,
                new MediaLinkItem(dialog, line.MessageId, line.MediaLabel))
        ];
    }
}
