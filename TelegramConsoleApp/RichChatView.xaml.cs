using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TelegramConsole.Core;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;

namespace TelegramConsoleApp;

public partial class RichChatView : System.Windows.Controls.UserControl
{
    private const int MaximumMessages = 300;
    private readonly Dictionary<int, RichChatMessageItem> _byMessageId = [];

    public ObservableCollection<RichChatMessageItem> Items { get; } = [];
    public event Action<ChatLine>? QuoteRequested;
    public event Action<ChatLine>? MediaOpenRequested;

    public RichChatView()
    {
        InitializeComponent();
        UpdateEmptyState();
    }

    public void ReplaceMessages(IEnumerable<ChatLine> messages)
    {
        var ordered = messages
            .GroupBy(x => x.MessageId)
            .Select(x => x.Last())
            .OrderBy(x => x.Time)
            .ThenBy(x => x.MessageId)
            .TakeLast(MaximumMessages)
            .Select(x => new RichChatMessageItem(x))
            .ToList();
        Items.Clear();
        _byMessageId.Clear();
        foreach (var item in ordered)
        {
            Items.Add(item);
            if (item.Line.MessageId > 0) _byMessageId[item.Line.MessageId] = item;
        }
        UpdateEmptyState();
        ScrollToEnd();
    }

    public void UpsertMessage(ChatLine line)
    {
        if (line.MessageId > 0 && _byMessageId.TryGetValue(line.MessageId, out var existing))
        {
            var index = Items.IndexOf(existing);
            var replacement = new RichChatMessageItem(line);
            Items[index] = replacement;
            _byMessageId[line.MessageId] = replacement;
            return;
        }

        var item = new RichChatMessageItem(line);
        var indexToInsert = Items.Count;
        while (indexToInsert > 0 && Compare(Items[indexToInsert - 1].Line, line) > 0) indexToInsert--;
        Items.Insert(indexToInsert, item);
        if (line.MessageId > 0) _byMessageId[line.MessageId] = item;
        while (Items.Count > MaximumMessages)
        {
            var removed = Items[0];
            Items.RemoveAt(0);
            if (removed.Line.MessageId > 0) _byMessageId.Remove(removed.Line.MessageId);
        }
        UpdateEmptyState();
        ScrollToEnd();
    }

    public void MarkDeleted(DeletedMessageInfo deleted)
    {
        if (!_byMessageId.TryGetValue(deleted.MessageId, out var existing)) return;
        var index = Items.IndexOf(existing);
        var replacement = new RichChatMessageItem(existing.Line, true);
        Items[index] = replacement;
        _byMessageId[deleted.MessageId] = replacement;
    }

    public void ClearMessages()
    {
        Items.Clear();
        _byMessageId.Clear();
        UpdateEmptyState();
    }

    private static int Compare(ChatLine left, ChatLine right)
    {
        var time = left.Time.CompareTo(right.Time);
        return time != 0 ? time : left.MessageId.CompareTo(right.MessageId);
    }

    private void ScrollToEnd() => Dispatcher.BeginInvoke(() =>
    {
        if (Items.Count > 0) MessageList.ScrollIntoView(Items[^1]);
    }, System.Windows.Threading.DispatcherPriority.Background);

    private void UpdateEmptyState() => EmptyState.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void QuoteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RichChatMessageItem item && !item.IsDeleted)
            QuoteRequested?.Invoke(item.Line);
    }

    private void MediaMenuItem_Click(object sender, RoutedEventArgs e) => RaiseMedia(sender);
    private void MediaButton_Click(object sender, RoutedEventArgs e) => RaiseMedia(sender);

    private void RaiseMedia(object sender)
    {
        if ((sender as FrameworkElement)?.Tag is RichChatMessageItem item && item.Line.HasDownloadableMedia)
            MediaOpenRequested?.Invoke(item.Line);
    }
}

public sealed class RichChatMessageItem
{
    public ChatLine Line { get; }
    public bool IsDeleted { get; }
    public string Sender => Line.IsOutgoing ? "我" : Line.Sender;
    public string TimeText => Line.Time.ToString("HH:mm");
    public string Text => Line.Text;
    public string ReplySender => Line.ReplySender;
    public string ReplyText => Line.ReplyText;
    public System.Windows.HorizontalAlignment Alignment => Line.IsOutgoing
        ? System.Windows.HorizontalAlignment.Right
        : System.Windows.HorizontalAlignment.Left;
    public Brush BubbleBrush => new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(Line.IsOutgoing ? "#D9FDD3" : "#FFFFFF"));
    public Brush BorderBrush => new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(Line.IsMentioned ? "#60A5FA" : "#D8E2EC"));
    public Brush SenderBrush => new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(Line.IsMentioned ? "#2563EB" : Line.IsOutgoing ? "#15803D" : "#0F6CBD"));
    public Brush TextBrush => new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#17212B"));
    public Visibility ReplyVisibility => string.IsNullOrWhiteSpace(Line.ReplyText) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility MediaVisibility => Line.Media is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility TextVisibility => IsDeleted || string.IsNullOrWhiteSpace(Line.Text) || Line.Text == Line.MediaLabel ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DeletedVisibility => IsDeleted ? Visibility.Visible : Visibility.Collapsed;
    public string MediaTitle => string.IsNullOrWhiteSpace(Line.Media?.FileName) ? Line.Media?.Label ?? Line.MediaLabel : Line.Media!.FileName;
    public string MediaDetails => FormatDetails(Line.Media);
    public Visibility MediaDetailsVisibility => string.IsNullOrWhiteSpace(MediaDetails) ? Visibility.Collapsed : Visibility.Visible;
    public string MediaIcon => Line.Media?.Kind switch
    {
        ChatMediaKind.Photo => "▧",
        ChatMediaKind.Video or ChatMediaKind.VideoNote => "▶",
        ChatMediaKind.Animation => "GIF",
        ChatMediaKind.Audio or ChatMediaKind.Voice => "♪",
        ChatMediaKind.Sticker => "☺",
        ChatMediaKind.Poll => "≡",
        ChatMediaKind.Location => "⌖",
        ChatMediaKind.Contact => "●",
        ChatMediaKind.WebPage => "↗",
        _ => "↓"
    };
    public Brush MediaBrush => new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(Line.Media?.Kind switch
    {
        ChatMediaKind.Photo => "#3B82F6",
        ChatMediaKind.Video or ChatMediaKind.VideoNote or ChatMediaKind.Animation => "#7C3AED",
        ChatMediaKind.Audio or ChatMediaKind.Voice => "#059669",
        _ => "#475569"
    }));

    public RichChatMessageItem(ChatLine line, bool isDeleted = false)
    {
        Line = line;
        IsDeleted = isDeleted;
    }

    private static string FormatDetails(ChatMediaInfo? media)
    {
        if (media is null) return "";
        var parts = new List<string>();
        if (media.DurationSeconds > 0) parts.Add(TimeSpan.FromSeconds(media.DurationSeconds).ToString(@"m\:ss"));
        if (media.Size > 0) parts.Add(media.Size >= 1024 * 1024 ? $"{media.Size / 1024d / 1024d:0.0} MB" : $"{media.Size / 1024d:0} KB");
        if (!string.IsNullOrWhiteSpace(media.MimeType)) parts.Add(media.MimeType);
        return string.Join(" · ", parts);
    }
}
