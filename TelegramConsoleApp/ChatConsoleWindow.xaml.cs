using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Collections.ObjectModel;

namespace TelegramConsoleApp;

public partial class ChatConsoleWindow : Window
{
    private const int QuoteHistoryLimit = 300;
    private readonly ITelegramService _telegram;
    private readonly DialogItem _dialog;
    private readonly ObservableCollection<QuoteTargetItem> _quoteTargets = [];

    public ChatConsoleWindow(ITelegramService telegram, DialogItem dialog)
    {
        InitializeComponent();
        _telegram = telegram;
        _dialog = dialog;
        QuoteTargetBox.ItemsSource = _quoteTargets;
        Title = $"Console - {dialog.Name}";
        PeerTitle.Text = dialog.Name;
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

    private async void SendButton_Click(object sender, RoutedEventArgs e) => await SendAsync(false);

    private async void QuoteReplyButton_Click(object sender, RoutedEventArgs e) => await SendAsync(true);

    private void ClearQuoteButton_Click(object sender, RoutedEventArgs e) => QuoteTargetBox.SelectedItem = null;

    private async void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SendAsync(false);
    }

    private async Task SendAsync(bool quoteReply)
    {
        var text = InputBox.Text.Trim();
        if (text.Length == 0) return;
        try
        {
            if (quoteReply && QuoteTargetBox.SelectedItem is not QuoteTargetItem)
                throw new InvalidOperationException(LocalizationManager.Text("SelectQuoteTarget"));
            SetSendEnabled(false);
            if (quoteReply && QuoteTargetBox.SelectedItem is QuoteTargetItem target)
            {
                await _telegram.SendReplyAsync(_dialog, target.MessageId, text, target.Text);
                AppendText($"[{DateTime.Now:HH:mm:ss}] 我 ↪ #{target.MessageId} {target.Sender}: {text}", Brushes.LimeGreen);
                QuoteTargetBox.SelectedItem = null;
            }
            else
            {
                await _telegram.SendAsync(_dialog, text);
                AppendText($"[{DateTime.Now:HH:mm:ss}] 我: {text}", Brushes.LimeGreen);
            }
            InputBox.Clear();
        }
        catch (Exception ex)
        {
            AppendText("[ERROR] " + UserMessageFormatter.From(ex), Brushes.OrangeRed);
        }
        finally
        {
            SetSendEnabled(true);
            InputBox.Focus();
        }
    }

    private void Append(ChatLine line)
    {
        AppendText(
            $"[{line.Time:HH:mm:ss}] {line.Sender}: {line.Text}",
            line.IsMentioned ? Brushes.DodgerBlue : line.IsOutgoing ? Brushes.LimeGreen : Brushes.White);
        AddQuoteTarget(line);
    }

    private void AddQuoteTarget(ChatLine line)
    {
        if (line.MessageId <= 0) return;
        var existing = _quoteTargets.FirstOrDefault(x => x.MessageId == line.MessageId);
        if (existing is not null) _quoteTargets.Remove(existing);
        _quoteTargets.Insert(0, QuoteTargetItem.From(line));
        while (_quoteTargets.Count > QuoteHistoryLimit) _quoteTargets.RemoveAt(_quoteTargets.Count - 1);
    }

    private void SetSendEnabled(bool enabled)
    {
        InputBox.IsEnabled = enabled;
        SendButton.IsEnabled = enabled;
        QuoteReplyButton.IsEnabled = enabled;
    }

    private void AppendText(string text, Brush? color = null)
        => ConsoleBox.AppendLine(text, color ?? Brushes.White);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.L && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ConsoleBox.ClearOutput();
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
        if (Application.Current.MainWindow is not Window mainWindow || !mainWindow.IsLoaded) return;
        if (!mainWindow.IsVisible) mainWindow.Show();
        if (mainWindow.WindowState == WindowState.Minimized) mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
        mainWindow.Topmost = true;
        mainWindow.Topmost = false;
        mainWindow.Focus();
    }
}
