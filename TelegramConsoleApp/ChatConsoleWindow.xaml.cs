using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TelegramConsoleApp;

public partial class ChatConsoleWindow : Window
{
    private const int QuoteHistoryLimit = 300;
    private readonly ITelegramService _telegram;
    private readonly DialogItem _dialog;
    private readonly MainWindow _sourceWorkspace;
    private readonly string _accountLabel;
    private QuoteTargetItem? _selectedQuoteTarget;
    private QuoteTargetItem? _contextQuoteTarget;

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
        Loaded += Window_Loaded;
        Closed += Window_Closed;
        _telegram.MessageReceived += Telegram_MessageReceived;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            foreach (var line in await _telegram.LoadHistoryAsync(_dialog, QuoteHistoryLimit)) Append(line);
            InputBox.Focus();
        }
        catch (Exception ex)
        {
            AppendText("[ERROR] " + UserMessageFormatter.From(ex), Brushes.OrangeRed);
        }
    }

    private void Telegram_MessageReceived(ChatLine line)
    {
        if (line.ChatId != _dialog.Id) return;
        Dispatcher.BeginInvoke(() => Append(line));
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e) => await SendAsync();

    private void ClearQuoteButton_Click(object sender, RoutedEventArgs e) => ClearQuoteSelection();

    private async void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (string.IsNullOrWhiteSpace(InputBox.Text)) return;
        await SendAsync();
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
            if (quoteTarget is not null)
            {
                await _telegram.SendReplyAsync(_dialog, quoteTarget.MessageId, text, quoteTarget.Text);
            }
            else
            {
                await _telegram.SendAsync(_dialog, text);
            }
            InputBox.Clear();
            ClearQuoteSelection();
        }
        catch (Exception ex)
        {
            AppendText("[ERROR] " + UserMessageFormatter.From(ex), Brushes.OrangeRed);
        }
        finally
        {
            SetSendEnabled(true);
            SendButton.Content = "SEND ↵";
            InputBox.Focus();
        }
    }

    private void Append(ChatLine line)
    {
        var body = $"[{line.Time:HH:mm:ss}] {line.Sender}: {line.DisplayText}";
        var color = line.IsMentioned ? Brushes.DodgerBlue : line.IsOutgoing ? Brushes.LimeGreen : Brushes.White;
        var messageTag = line.MessageId > 0 ? QuoteTargetItem.From(line) : null;
        object? deduplicationKey = line.MessageId > 0
            ? (line.ChatKind, line.ChatId, line.MessageId, line.DisplayText)
            : null;
        if (line.ReplyToMessageId is not int replyId)
        {
            ConsoleBox.AppendLines(
                [(body, color, messageTag)], deduplicationKey,
                MediaLinkFactory.Create(line, body, lineIndex: 0));
            return;
        }
        var sender = string.IsNullOrWhiteSpace(line.ReplySender) ? $"消息 #{replyId}" : line.ReplySender;
        var value = line.ReplyText.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (value.Length > 120) value = value[..117] + "...";
        ConsoleBox.AppendLines(
        [
            ($"↪ {sender}: {value}", Brushes.Gray, new QuoteTargetItem(replyId, sender, line.ReplyText)),
            (body, color, messageTag)
        ], deduplicationKey, MediaLinkFactory.Create(line, body, lineIndex: 1));
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
                Close();
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
        _telegram.MessageReceived -= Telegram_MessageReceived;
        if (!_sourceWorkspace.IsLoaded) return;
        _sourceWorkspace.ShowWorkspace();
        _sourceWorkspace.Topmost = true;
        _sourceWorkspace.Topmost = false;
        _sourceWorkspace.Focus();
    }
}
