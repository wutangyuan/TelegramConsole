namespace TelegramConsoleApp;

internal sealed record QuoteTargetItem(int MessageId, string Sender, string Text)
{
    public string DisplayText => $"#{MessageId} {Sender}: {Preview(Text)}";

    public static QuoteTargetItem From(ChatLine line) => new(line.MessageId, line.Sender, line.DisplayText);

    private static string Preview(string text)
    {
        var singleLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 80 ? singleLine : singleLine[..77] + "...";
    }
}
