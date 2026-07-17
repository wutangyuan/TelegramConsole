using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TelegramConsoleApp;

public partial class ChatConsoleWindow : Window
{
    private const int QuoteHistoryLimit = 300;
    private readonly ITelegramService _telegram;
    private readonly DialogItem _dialog;
    private readonly MainWindow _sourceWorkspace;
    private readonly string _accountLabel;
    private readonly List<ChatLine> _timeline = [];
    private readonly List<TimelineNotice> _notices = [];
    private readonly DispatcherTimer _timelineRenderTimer;
    private QuoteTargetItem? _selectedQuoteTarget;
    private QuoteTargetItem? _contextQuoteTarget;
    private bool _historyLoaded;
    private bool _suppressWorkspaceRestore;
    private DateTime _pendingSentScrollUntilUtc;

    public ChatConsoleWindow(
        ITelegramService telegram,
        DialogItem dialog,
        MainWindow sourceWorkspace,
        string accountLabel)
    {
        InitializeComponent();
        _telegram = telegram;
        _dialog = dialog;
        _sourceWorkspace = sourceWorkspace;
        _accountLabel = string.IsNullOrWhiteSpace(accountLabel) ? telegram.CurrentUser : accountLabel;
        Title = $"Console - {_accountLabel} - {dialog.Name}";
        PeerTitle.Text = dialog.Name;
        AccountTitle.Text = $"账户：{_accountLabel}";
        AccountStatusText.Text = $"账户：{_accountLabel}";
        _timelineRenderTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(80)
        };
        _timelineRenderTimer.Tick += (_, _) =>
        {
            _timelineRenderTimer.Stop();
            RenderTimeline();
        };
        Loaded += Window_Loaded;
        Closed += Window_Closed;
        _telegram.MessageReceived += Telegram_MessageReceived;
        _telegram.MessageDeleted += Telegram_MessageDeleted;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            MergeTimeline(await _telegram.LoadHistoryAsync(_dialog, QuoteHistoryLimit));
            _historyLoaded = true;
            RenderTimeline(forceScrollToEnd: true);
            InputBox.Focus();
        }
        catch (Exception ex)
        {
            _historyLoaded = true;
            RenderTimeline(forceScrollToEnd: true);
            AppendText("[ERROR] " + UserMessageFormatter.From(ex), Brushes.OrangeRed);
        }
    }

    private void Telegram_MessageReceived(ChatLine line)
    {
        if (line.ChatId != _dialog.Id) return;
        Dispatcher.BeginInvoke(() =>
        {
            var canAppend = MergeTimeline([line]);
            if (!_historyLoaded) return;
            var forceScrollToEnd = line.IsOutgoing && DateTime.UtcNow <= _pendingSentScrollUntilUtc;
            if (forceScrollToEnd) _pendingSentScrollUntilUtc = default;
            if (canAppend) Append(line, forceScrollToEnd);
            else if (forceScrollToEnd) RenderTimeline(forceScrollToEnd: true);
            else if (!_timelineRenderTimer.IsEnabled) _timelineRenderTimer.Start();
        });
    }

    private void Telegram_MessageDeleted(MessageDeletion deletion)
    {
        if (deletion.ChatId != _dialog.Id) return;
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var message in deletion.Messages)
            {
                var sender = string.IsNullOrWhiteSpace(message.Sender) ? "未知发送人" : message.Sender;
                _notices.Add(new TimelineNotice(
                    deletion.Time,
                    message.MessageId,
                    new TerminalBlock(
                        [($"[{deletion.Time:HH:mm:ss}] ↩ 已撤回 #{message.MessageId} {sender}: {message.Text}",
                            Brushes.Orange, null)])));
            }
            if (_notices.Count > 100) _notices.RemoveRange(0, _notices.Count - 100);
            RenderTimeline();
        });
    }

    private bool MergeTimeline(IEnumerable<ChatLine> lines)
    {
        var canAppend = true;
        foreach (var line in lines)
        {
            var existingIndex = line.MessageId > 0
                ? _timeline.FindIndex(x => x.MessageId == line.MessageId &&
                                           x.ChatId == line.ChatId &&
                                           string.Equals(x.ChatKind, line.ChatKind, StringComparison.Ordinal))
                : -1;
            if (existingIndex >= 0)
            {
                _timeline[existingIndex] = line;
                canAppend = false;
            }
            else
            {
                var previous = _timeline.LastOrDefault();
                if (previous is not null &&
                    (line.Time < previous.Time ||
                     line.Time == previous.Time && line.MessageId > 0 && previous.MessageId > line.MessageId))
                    canAppend = false;
                if (_notices.Count > 0 && line.Time < _notices.Max(x => x.Time))
                    canAppend = false;
                _timeline.Add(line);
            }
        }

        var ordered = _timeline
            .OrderBy(x => x.Time)
            .ThenBy(x => x.MessageId > 0 ? x.MessageId : int.MaxValue)
            .TakeLast(QuoteHistoryLimit)
            .ToArray();
        _timeline.Clear();
        _timeline.AddRange(ordered);
        return canAppend;
    }

    private void RenderTimeline(bool forceScrollToEnd = false)
    {
        var blocks = _timeline
            .Select(x => new TimelineRenderItem(x.Time, x.MessageId, 0, CreateBlock(x)))
            .Concat(_notices.Select((x, index) =>
                new TimelineRenderItem(x.Time, x.MessageId, index + 1, x.Block)))
            .OrderBy(x => x.Time)
            .ThenBy(x => x.MessageId > 0 ? x.MessageId : int.MaxValue)
            .ThenBy(x => x.Sequence)
            .Select(x => x.Block)
            .ToArray();
        ConsoleBox.ReplaceBlocks(blocks, forceScrollToEnd);
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e) => await SendAsync();

    private void ClearQuoteButton_Click(object sender, RoutedEventArgs e) => ClearQuoteSelection();

    private async void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key != Key.Enter) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            e.Handled = true;
            InsertLineBreak(InputBox);
            return;
        }
        e.Handled = true;
        if (string.IsNullOrWhiteSpace(InputBox.Text)) return;
        await SendAsync();
    }

    private static void InsertLineBreak(System.Windows.Controls.TextBox textBox)
    {
        var start = textBox.SelectionStart;
        var length = textBox.SelectionLength;
        var lineBreak = Environment.NewLine;
        textBox.Text = textBox.Text.Remove(start, length).Insert(start, lineBreak);
        textBox.CaretIndex = start + lineBreak.Length;
        textBox.SelectionLength = 0;
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateSendButton();

    private async Task SendAsync()
    {
        var text = InputBox.Text.Trim();
        try
        {
            if (text.Length == 0)
                throw new InvalidOperationException(LocalizationManager.Text("MessageRequired"));
            SetSendEnabled(false);
            SendButton.Content = LocalizationManager.Text("SendingMessage");
            var quoteTarget = _selectedQuoteTarget;
            _pendingSentScrollUntilUtc = DateTime.UtcNow.AddSeconds(30);
            if (quoteTarget is not null)
            {
                await _telegram.SendReplyAsync(_dialog, quoteTarget.MessageId, text, quoteTarget.Text);
            }
            else
            {
                await _telegram.SendAsync(_dialog, text);
            }
            ConsoleBox.ScrollToEnd();
            InputBox.Clear();
            ClearQuoteSelection();
        }
        catch (Exception ex)
        {
            _pendingSentScrollUntilUtc = default;
            AppendText("[ERROR] " + UserMessageFormatter.From(ex), Brushes.OrangeRed);
        }
        finally
        {
            SetSendEnabled(true);
            SendButton.Content = "SEND ↵";
            InputBox.Focus();
        }
    }

    private void Append(ChatLine line, bool forceScrollToEnd = false)
    {
        var block = CreateBlock(line);
        ConsoleBox.AppendLines(block.Lines, block.DeduplicationKey, block.InlineLinks, forceScrollToEnd);
    }

    private static TerminalBlock CreateBlock(ChatLine line)
    {
        var body = $"[{line.Time:HH:mm:ss}] {line.Sender}: {line.DisplayText}";
        if (line.IsEdited) body += "  [已编辑]";
        body += FormatReactions(line.Reactions);
        var color = line.IsMentioned ? Brushes.DodgerBlue : line.IsOutgoing ? Brushes.LimeGreen : Brushes.White;
        var messageTag = line.MessageId > 0 ? QuoteTargetItem.From(line) : null;
        object? deduplicationKey = line.MessageId > 0
            ? (line.ChatKind, line.ChatId, line.MessageId, line.DisplayText)
            : null;
        if (line.ReplyToMessageId is not int replyId)
        {
            return new TerminalBlock(
                [(body, color, messageTag)], deduplicationKey,
                MediaLinkFactory.Create(line, body, lineIndex: 0));
        }
        var sender = string.IsNullOrWhiteSpace(line.ReplySender) ? $"消息 #{replyId}" : line.ReplySender;
        var value = line.ReplyText.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (value.Length > 120) value = value[..117] + "...";
        return new TerminalBlock(
        [
            ($"↪ {sender}: {value}", Brushes.Gray, new QuoteTargetItem(replyId, sender, line.ReplyText)),
            (body, color, messageTag)
        ], deduplicationKey, MediaLinkFactory.Create(line, body, lineIndex: 1));
    }

    private static string FormatReactions(IReadOnlyList<ChatReaction>? reactions)
    {
        if (reactions is null || reactions.Count == 0) return "";
        return "  " + string.Join(" ", reactions.Select(x => x.Count > 1 ? $"{x.Symbol}×{x.Count}" : x.Symbol));
    }

    private void ConsoleBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _contextQuoteTarget = ConsoleBox.GetTagAtVisualPosition<QuoteTargetItem>(
            e.GetPosition(ConsoleBox.TextArea.TextView));
    }

    private async void ConsoleBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var media = ConsoleBox.GetTagAtVisualPosition<MediaLinkItem>(
            e.GetPosition(ConsoleBox.TextArea.TextView), preferSelection: false);
        if (media is null) return;
        e.Handled = true;
        try
        {
            PeerTitle.Text = LocalizationManager.Format("DownloadingMedia", media.Label);
            var path = await _telegram.DownloadMediaAsync(media.Dialog, media.MessageId);
            MediaFileLauncher.Open(this, path);
        }
        catch (Exception ex)
        {
            AppendText("[ERROR] " + UserMessageFormatter.From(ex), Brushes.OrangeRed);
        }
        finally
        {
            PeerTitle.Text = _dialog.Name;
        }
    }

    private void ConsoleBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        ConsoleQuoteMenuItem.IsEnabled = _contextQuoteTarget is not null;
    }

    private void ConsoleQuoteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextQuoteTarget is null) return;
        _selectedQuoteTarget = _contextQuoteTarget;
        QuotePreviewText.Text = $"↪ {_selectedQuoteTarget.DisplayText}";
        QuotePreviewPanel.Visibility = Visibility.Visible;
        InputBox.Focus();
    }

    private void ClearQuoteSelection()
    {
        _selectedQuoteTarget = null;
        _contextQuoteTarget = null;
        QuotePreviewText.Text = string.Empty;
        QuotePreviewPanel.Visibility = Visibility.Collapsed;
    }

    private void SetSendEnabled(bool enabled)
    {
        InputBox.IsEnabled = enabled;
        SendButton.IsEnabled = enabled && !string.IsNullOrWhiteSpace(InputBox.Text);
    }

    private void UpdateSendButton() =>
        SendButton.IsEnabled = InputBox.IsEnabled && !string.IsNullOrWhiteSpace(InputBox.Text);

    private void AppendText(
        string text,
        Brush? color = null,
        object? tag = null,
        object? deduplicationKey = null)
        => ConsoleBox.AppendLine(text, color ?? Brushes.White, tag, deduplicationKey);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_selectedQuoteTarget is not null)
                ClearQuoteSelection();
            else
            {
                _suppressWorkspaceRestore = true;
                Close();
            }
            e.Handled = true;
        }
        else if (e.Key == Key.L && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ConsoleBox.ClearOutput();
            ClearQuoteSelection();
            e.Handled = true;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _timelineRenderTimer.Stop();
        _telegram.MessageReceived -= Telegram_MessageReceived;
        _telegram.MessageDeleted -= Telegram_MessageDeleted;
        if (_suppressWorkspaceRestore) return;
        if (!_sourceWorkspace.IsLoaded) return;
        _sourceWorkspace.ShowWorkspace();
        _sourceWorkspace.Topmost = true;
        _sourceWorkspace.Topmost = false;
        _sourceWorkspace.Focus();
    }

    private sealed record TimelineNotice(DateTime Time, int MessageId, TerminalBlock Block);
    private sealed record TimelineRenderItem(DateTime Time, int MessageId, int Sequence, TerminalBlock Block);
}
