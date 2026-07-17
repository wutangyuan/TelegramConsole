using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace TelegramConsoleApp;

public sealed partial class LinkTextBlock : TextBlock
{
    public static readonly DependencyProperty LinkTextProperty = DependencyProperty.Register(
        nameof(LinkText), typeof(string), typeof(LinkTextBlock),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure, OnLinkTextChanged));

    public string LinkText
    {
        get => (string)GetValue(LinkTextProperty);
        set => SetValue(LinkTextProperty, value);
    }

    private static void OnLinkTextChanged(DependencyObject source, DependencyPropertyChangedEventArgs args) =>
        ((LinkTextBlock)source).RenderText(args.NewValue as string ?? "");

    private void RenderText(string text)
    {
        Inlines.Clear();
        var position = 0;
        foreach (Match match in UrlPattern().Matches(text))
        {
            if (match.Index > position) Inlines.Add(new Run(text[position..match.Index]));
            var url = match.Value;
            var link = new Hyperlink(new Run(url))
            {
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235)),
                TextDecorations = null,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = url
            };
            link.Click += (_, _) => Open(url);
            Inlines.Add(link);
            position = match.Index + match.Length;
        }
        if (position < text.Length) Inlines.Add(new Run(text[position..]));
    }

    private static void Open(string url)
    {
        if (url.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* 链接打开失败不影响消息视图。 */ }
    }

    [GeneratedRegex(@"(?i)\b(?:https?://|tg://|www\.)[^\s<>]+", RegexOptions.Compiled)]
    private static partial Regex UrlPattern();
}
