using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TelegramConsole.Core;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;

namespace TelegramConsoleApp;

public partial class RichChatView : System.Windows.Controls.UserControl
{
    private const int MaximumMessages = 300;
    private readonly Dictionary<int, RichChatMessageItem> _byMessageId = [];
    private readonly HashSet<int> _previewRequests = [];

    public ObservableCollection<RichChatMessageItem> Items { get; } = [];
    public event Action<ChatLine>? QuoteRequested;
    public event Action<ChatLine>? MediaOpenRequested;
    public event Action<ChatLine>? PreviewRequested;
    public event Action<ChatLine>? EditRequested;
    public event Action<ChatLine>? DeleteRequested;
    public event Action<ChatLine>? CopyLinkRequested;
    public event Action<ChatLine>? ForwardRequested;
    public event Action<ChatLine, string>? ReactionRequested;

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
        _previewRequests.Clear();
        foreach (var item in ordered)
        {
            Items.Add(item);
            if (item.Line.MessageId > 0) _byMessageId[item.Line.MessageId] = item;
        }
        RefreshDateSeparators();
        UpdateEmptyState();
        ScrollToEnd();
    }

    public void UpsertMessage(ChatLine line)
    {
        if (line.MessageId > 0 && _byMessageId.TryGetValue(line.MessageId, out var existing))
        {
            var index = Items.IndexOf(existing);
            var replacement = new RichChatMessageItem(line);
            replacement.CopyPreviewFrom(existing);
            Items[index] = replacement;
            _byMessageId[line.MessageId] = replacement;
            RefreshDateSeparators();
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
        RefreshDateSeparators();
        UpdateEmptyState();
        ScrollToEnd();
    }

    public void MarkDeleted(DeletedMessageInfo deleted)
    {
        if (!_byMessageId.TryGetValue(deleted.MessageId, out var existing)) return;
        var index = Items.IndexOf(existing);
        var replacement = new RichChatMessageItem(existing.Line, true);
        replacement.CopyPreviewFrom(existing);
        Items[index] = replacement;
        _byMessageId[deleted.MessageId] = replacement;
    }

    public void ClearMessages()
    {
        Items.Clear();
        _byMessageId.Clear();
        _previewRequests.Clear();
        UpdateEmptyState();
    }

    public void SetPreview(int messageId, string? path)
    {
        if (!_byMessageId.TryGetValue(messageId, out var item) || string.IsNullOrWhiteSpace(path)) return;
        item.SetPreview(path);
    }

    private void RefreshDateSeparators()
    {
        DateTime? previous = null;
        foreach (var item in Items)
        {
            item.ShowDate = previous != item.Line.Time.Date;
            previous = item.Line.Time.Date;
        }
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

    private void ReplyPreview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RichChatMessageItem item ||
            item.Line.ReplyToMessageId is not int replyId || !_byMessageId.TryGetValue(replyId, out var target)) return;
        MessageList.ScrollIntoView(target);
        MessageList.SelectedItem = target;
        e.Handled = true;
    }

    private void MediaMenuItem_Click(object sender, RoutedEventArgs e) => RaiseMedia(sender);
    private void MediaButton_Click(object sender, RoutedEventArgs e) => RaiseMedia(sender);

    private void MediaButton_Loaded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RichChatMessageItem item ||
            !item.SupportsPreview || item.Line.MessageId <= 0 || !_previewRequests.Add(item.Line.MessageId)) return;
        PreviewRequested?.Invoke(item.Line);
    }

    private void EditMenuItem_Click(object sender, RoutedEventArgs e) => RaiseLine(sender, EditRequested);
    private void DeleteMenuItem_Click(object sender, RoutedEventArgs e) => RaiseLine(sender, DeleteRequested);
    private void CopyLinkMenuItem_Click(object sender, RoutedEventArgs e) => RaiseLine(sender, CopyLinkRequested);
    private void ForwardMenuItem_Click(object sender, RoutedEventArgs e) => RaiseLine(sender, ForwardRequested);

    private void ReactionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: RichChatMessageItem item, Header: string emoji } && !item.IsDeleted)
            ReactionRequested?.Invoke(item.Line, emoji);
    }

    private void CopyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RichChatMessageItem item && !string.IsNullOrWhiteSpace(item.Line.DisplayText))
            System.Windows.Clipboard.SetText(item.Line.DisplayText);
    }

    private static void RaiseLine(object sender, Action<ChatLine>? handler)
    {
        if ((sender as FrameworkElement)?.Tag is RichChatMessageItem item && !item.IsDeleted)
            handler?.Invoke(item.Line);
    }

    private void RaiseMedia(object sender)
    {
        if ((sender as FrameworkElement)?.Tag is RichChatMessageItem item && item.Line.HasDownloadableMedia)
            MediaOpenRequested?.Invoke(item.Line);
    }
}

public sealed class RichChatMessageItem : INotifyPropertyChanged
{
    private ImageSource? _previewImage;
    private bool _showDate;
    public ChatLine Line { get; }
    public bool IsDeleted { get; }
    public string Sender
    {
        get
        {
            var sender = Line.IsOutgoing ? "我" : Line.Sender;
            return string.IsNullOrWhiteSpace(Line.AuthorSignature) ? sender : $"{sender} · {Line.AuthorSignature}";
        }
    }
    public string TimeText => Line.Time.ToString("HH:mm");
    public string DateText => Line.Time.Date == DateTime.Today
        ? (Application.Current.TryFindResource("Today") as string ?? "今天")
        : Line.Time.ToString("yyyy-MM-dd dddd");
    public bool ShowDate
    {
        get => _showDate;
        set
        {
            if (_showDate == value) return;
            _showDate = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DateVisibility));
        }
    }
    public Visibility DateVisibility => ShowDate ? Visibility.Visible : Visibility.Collapsed;
    public string Text => Line.Text;
    public string ReplySender => Line.ReplySender;
    public string ReplyText => Line.ReplyText;
    public string ForwardedLabel => $"{(Application.Current.TryFindResource("ForwardedFrom") as string ?? "转发自")} {Line.ForwardedFrom}";
    public string EditedText => Application.Current.TryFindResource("Edited") as string ?? "已编辑";
    public string StatisticsText => string.Join(" · ", new[]
    {
        Line.ViewCount > 0 ? $"◉ {Line.ViewCount}" : "",
        Line.ForwardCount > 0 ? $"↗ {Line.ForwardCount}" : ""
    }.Where(x => x.Length > 0));
    public System.Windows.HorizontalAlignment Alignment => Line.IsOutgoing
        ? System.Windows.HorizontalAlignment.Right
        : System.Windows.HorizontalAlignment.Left;
    public Brush BubbleBrush => new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(Line.IsOutgoing ? "#D9FDD3" : "#FFFFFF"));
    public Brush BorderBrush => new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(Line.IsMentioned ? "#60A5FA" : "#D8E2EC"));
    public Brush SenderBrush => new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(Line.IsMentioned ? "#2563EB" : Line.IsOutgoing ? "#15803D" : "#0F6CBD"));
    public Brush TextBrush => new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#17212B"));
    public Visibility ReplyVisibility => string.IsNullOrWhiteSpace(Line.ReplyText) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ForwardVisibility => string.IsNullOrWhiteSpace(Line.ForwardedFrom) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility OutgoingVisibility => Line.IsOutgoing ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MediaVisibility => Line.Media is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility TextVisibility => IsDeleted || string.IsNullOrWhiteSpace(Line.Text) || Line.Text == Line.MediaLabel ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DeletedVisibility => IsDeleted ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EditedVisibility => Line.IsEdited ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PinnedVisibility => Line.IsPinned ? Visibility.Visible : Visibility.Collapsed;
    public Visibility StatisticsVisibility => string.IsNullOrWhiteSpace(StatisticsText) ? Visibility.Collapsed : Visibility.Visible;
    public IReadOnlyList<RichReactionItem> Reactions => (Line.Reactions ?? []).Select(x => new RichReactionItem(
        $"{x.Symbol} {x.Count}",
        BrushOf(x.IsChosen ? "#DBEAFE" : "#F1F5F9"),
        BrushOf(x.IsChosen ? "#3B82F6" : "#CBD5E1"))).ToArray();
    public Visibility ReactionsVisibility => Reactions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool SupportsPreview => Line.Media?.Kind is ChatMediaKind.Photo or ChatMediaKind.Video or ChatMediaKind.VideoNote
        or ChatMediaKind.Animation or ChatMediaKind.Sticker;
    public ImageSource? PreviewImage => _previewImage;
    public Visibility PreviewVisibility => _previewImage is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility MediaPlaceholderVisibility => _previewImage is null ? Visibility.Visible : Visibility.Collapsed;
    public string MediaTitle => !string.IsNullOrWhiteSpace(Line.Media?.Title)
        ? Line.Media.Title
        : string.IsNullOrWhiteSpace(Line.Media?.FileName) ? Line.Media?.Label ?? Line.MediaLabel : Line.Media!.FileName;
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

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetPreview(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 720;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        _previewImage = image;
        OnPropertyChanged(nameof(PreviewImage));
        OnPropertyChanged(nameof(PreviewVisibility));
        OnPropertyChanged(nameof(MediaPlaceholderVisibility));
    }

    public void CopyPreviewFrom(RichChatMessageItem source)
    {
        if (source._previewImage is null) return;
        _previewImage = source._previewImage;
        OnPropertyChanged(nameof(PreviewImage));
        OnPropertyChanged(nameof(PreviewVisibility));
        OnPropertyChanged(nameof(MediaPlaceholderVisibility));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static Brush BrushOf(string color) => new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(color));

    private static string FormatDetails(ChatMediaInfo? media)
    {
        if (media is null) return "";
        var parts = new List<string>();
        if (media.DurationSeconds > 0) parts.Add(TimeSpan.FromSeconds(media.DurationSeconds).ToString(@"m\:ss"));
        if (media.Size > 0) parts.Add(media.Size >= 1024 * 1024 ? $"{media.Size / 1024d / 1024d:0.0} MB" : $"{media.Size / 1024d:0} KB");
        if (!string.IsNullOrWhiteSpace(media.MimeType)) parts.Add(media.MimeType);
        if (!string.IsNullOrWhiteSpace(media.Description)) parts.Add(media.Description);
        return string.Join(" · ", parts);
    }
}

public sealed record RichReactionItem(string Text, Brush Background, Brush BorderBrush);
