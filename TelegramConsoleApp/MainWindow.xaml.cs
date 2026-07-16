using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Diagnostics;
using System.IO;

namespace TelegramConsoleApp;

public partial class MainWindow : Window
{
    private const int QuoteHistoryLimit = 300;
    private readonly ISettingsStore _store = new SettingsStore();
    private readonly IAppLogger _logger = new Log4NetAppLogger();
    private readonly AppSettings _settings;
    private readonly ITelegramService _telegram;
    private readonly ISchedulerService _scheduler;
    private readonly IExceptionMonitorService _exceptionMonitor;
    private readonly IMentionMonitorService _mentionMonitor;
    private AccountProfile? _activeAccount;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _exitRequested;
    private bool _trayHintShown;
    private List<DialogItem> _allDialogs = [];
    private QuoteTargetItem? _selectedQuoteTarget;
    private QuoteTargetItem? _contextQuoteTarget;
    private bool _loadingHistory;
    private bool _initialized;
    private int _exceptionQueryLimit = 10;
    private GridLength _expandedDialogWidth = new(290);

    public MainWindow()
    {
        InitializeComponent();
        InitializeTrayIcon();
        _settings = _store.Load();
        _telegram = new TelegramService(_store, _logger);
        _exceptionMonitor = new ExceptionMonitorService(_store, _logger, _telegram, _settings);
        _mentionMonitor = new MentionMonitorService(_store, _telegram, _logger);
        _scheduler = new SchedulerService(_telegram, _store, _settings, _logger);

        ApiIdBox.Text = _settings.ApiId == 0 ? "" : _settings.ApiId.ToString();
        ApiHashBox.Password = _settings.ApiHash;
        PhoneBox.Text = _settings.PhoneNumber;
        MonitorEnabledBox.IsChecked = _settings.MonitorEnabled;
        ExceptionNotifyEnabledBox.IsChecked = true;
        ExceptionMinimumLevelBox.SelectedIndex = 0;
        ExceptionQueryLevelBox.SelectedIndex = 0;
        ExceptionFromDatePicker.SelectedDate = DateTime.Today;
        ExceptionToDatePicker.SelectedDate = DateTime.Today;
        ExceptionEmailBox.Text = "";
        MentionNotifyEnabledBox.IsChecked = false;
        SchedulePeriodBox.SelectedIndex = 0;
        AddMonday.IsChecked = true;
        RenderSchedules();
        SetStatus(L("ConfigureTelegram"));

        _telegram.MessageReceived += line => Dispatcher.BeginInvoke(() => HandleIncoming(line));
        _telegram.Log += text => Dispatcher.BeginInvoke(() => SetStatus(text));
        _telegram.ConnectionStateChanged += state => Dispatcher.BeginInvoke(() => HandleConnectionState(state));
        _telegram.OutboxChanged += Telegram_OutboxChanged;
        _telegram.AutomationActivity += Telegram_AutomationActivity;
        _scheduler.Status += text => Dispatcher.BeginInvoke(() =>
        {
            SetStatus(text);
            AppendConsole(MonitorConsole, $"[{DateTime.Now:HH:mm:ss}] [任务] {text}");
        });
        _logger.EntryWritten += Logger_EntryWritten;
        _exceptionMonitor.RecordsChanged += ExceptionMonitor_RecordsChanged;
        _mentionMonitor.RecordsChanged += MentionMonitor_RecordsChanged;
        Application.Current.DispatcherUnhandledException += Application_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        _logger.Info("Application", "WPF 主窗口已初始化");
        Closing += MainWindow_Closing;
        Loaded += async (_, _) => await RefreshExceptionsAsync();
        _initialized = true;
    }

    private void InitializeTrayIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/AppIcon.ico"))
            ?? throw new InvalidOperationException("找不到应用图标资源");
        using var sourceIcon = new System.Drawing.Icon(resource.Stream);
        var trayMenu = new System.Windows.Forms.ContextMenuStrip();
        trayMenu.Items.Add(L("ShowMainWindow"), null, (_, _) => Dispatcher.BeginInvoke(ShowMainWindow));
        trayMenu.Items.Add(L("ExitApplication"), null, (_, _) => Dispatcher.BeginInvoke(ExitApplication));
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = (System.Drawing.Icon)sourceIcon.Clone(),
            Text = "Telegram 控制台助手",
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowMainWindow);
    }

    private void ShowMainWindow()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        Close();
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync(BeginLoginAsync);

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_settings, _store) { Owner = this };
        if (window.ShowDialog() == true)
        {
            RenderSchedules();
            SetStatus(_settings.Proxy.Enabled
                ? LF("ProxySaved", _settings.Proxy.Type == "MtProxy" ? "MTProxy" : $"SOCKS5 {_settings.Proxy.Host}:{_settings.Proxy.Port}")
                : L("ProxyDisabled"));
        }
    }

    private void ProductivityTools_Click(object sender, RoutedEventArgs e)
    {
        if (_activeAccount is null)
        {
            ShowError(L("LoginFirst"));
            return;
        }
        new ProductivityWindow(_telegram, _allDialogs, _activeAccount, _store, _settings, _logger)
        {
            Owner = this
        }.Show();
    }

    private async Task BeginLoginAsync()
    {
        if (!int.TryParse(ApiIdBox.Text.Trim(), out var apiId) || apiId <= 0)
            throw new InvalidOperationException(L("ApiIdInvalid"));
        if (string.IsNullOrWhiteSpace(ApiHashBox.Password) || string.IsNullOrWhiteSpace(PhoneBox.Text))
            throw new InvalidOperationException(L("FillLoginSettings"));

        _settings.ApiId = apiId;
        _settings.ApiHash = ApiHashBox.Password.Trim();
        _settings.PhoneNumber = PhoneBox.Text.Trim();
        _store.Save(_settings);
        await DeactivateAccountAsync();
        SetLoginBusy(true);
        SetStatus(L("ConnectingTelegram"));
        await HandleLoginResultAsync(await _telegram.BeginLoginAsync(_settings));
    }

    private async Task DeactivateAccountAsync()
    {
        _activeAccount = null;
        _exceptionMonitor.DeactivateAccount();
        _mentionMonitor.DeactivateAccount();
        _telegram.ConfigureAutomationRules([]);
        await _scheduler.DeactivateAccountAsync();
        _allDialogs = [];
        DialogsList.ItemsSource = null;
        ScheduleChatBox.ItemsSource = null;
        ConfirmationPeerBox.ItemsSource = null;
        ExceptionPeerBox.ItemsSource = null;
        MentionTargetBox.ItemsSource = null;
        ExceptionNotifyEnabledBox.IsChecked = false;
        ExceptionMinimumLevelBox.SelectedIndex = 0;
        ExceptionEmailBox.Clear();
        MentionNotifyEnabledBox.IsChecked = false;
        ScheduleList.ItemsSource = null;
        ExceptionList.ItemsSource = null;
        MentionList.ItemsSource = null;
        OutboxList.ItemsSource = null;
        ClearQuoteSelection();
        ChatConsole.ClearOutput();
        MonitorConsole.ClearOutput();
    }

    private async void ContinueLoginButton_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync(ContinueLoginAsync);

    private async Task ContinueLoginAsync()
    {
        var value = LoginPasswordBox.Visibility == Visibility.Visible
            ? LoginPasswordBox.Password
            : LoginValueBox.Text;
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException(L("LoginInputRequired"));
        ContinueLoginButton.IsEnabled = false;
        await HandleLoginResultAsync(await _telegram.ContinueLoginAsync(value.Trim()));
    }

    private async Task HandleLoginResultAsync(string? prompt)
    {
        if (prompt is not null)
        {
            var isPassword = prompt == "password";
            LoginPromptLabel.Text = PromptText(prompt);
            LoginValueBox.Text = "";
            LoginPasswordBox.Password = "";
            LoginPromptLabel.Visibility = Visibility.Visible;
            LoginValueBox.Visibility = isPassword ? Visibility.Collapsed : Visibility.Visible;
            LoginPasswordBox.Visibility = isPassword ? Visibility.Visible : Visibility.Collapsed;
            ContinueLoginButton.Visibility = Visibility.Visible;
            ContinueLoginButton.IsEnabled = true;
            if (isPassword) LoginPasswordBox.Focus(); else LoginValueBox.Focus();
            SetStatus(LF("TelegramRequires", PromptText(prompt)));
            return;
        }

        LoginPromptLabel.Visibility = LoginValueBox.Visibility = LoginPasswordBox.Visibility =
            ContinueLoginButton.Visibility = Visibility.Collapsed;
        SetLoginBusy(false);
        SetStatus(LF("LoggedIn", _telegram.CurrentUser));
        ShowAuthenticatedAccount();
        await ActivateAccountAsync();
        await LoadDialogsAsync();
        await _scheduler.RunDueTasksAsync();
    }

    private void ShowAuthenticatedAccount()
    {
        LoggedInAccountText.Text = _telegram.CurrentUser;
        ConnectionStatusText.Text = L("Online");
        ConnectionStatusText.Foreground = Brushes.ForestGreen;
        ConnectionStatusDot.Background = Brushes.LimeGreen;
        LoginFormPanel.Visibility = Visibility.Collapsed;
        LoggedInPanel.Visibility = Visibility.Visible;
        LoginButton.IsEnabled = false;
    }

    private void HandleConnectionState(TelegramConnectionState state)
    {
        switch (state.Status)
        {
            case TelegramConnectionStatus.Connecting:
                if (LoggedInPanel.Visibility == Visibility.Visible)
                {
                    ConnectionStatusText.Text = L("ConnectingStatus");
                    ConnectionStatusText.Foreground = Brushes.DarkOrange;
                    ConnectionStatusDot.Background = Brushes.DarkOrange;
                }
                SetStatus(state.Message, Brushes.DarkOrange);
                break;
            case TelegramConnectionStatus.Recovering:
                if (_telegram.CurrentUserId != 0)
                {
                    LoggedInAccountText.Text = _telegram.CurrentUser;
                    LoginFormPanel.Visibility = Visibility.Collapsed;
                    LoggedInPanel.Visibility = Visibility.Visible;
                    ConnectionStatusText.Text = L("RecoveringStatus");
                    ConnectionStatusText.Foreground = Brushes.DarkOrange;
                    ConnectionStatusDot.Background = Brushes.DarkOrange;
                }
                SetStatus(state.Message, Brushes.DarkOrange);
                break;
            case TelegramConnectionStatus.Connected:
                if (_telegram.CurrentUserId != 0) ShowAuthenticatedAccount();
                SetStatus(state.Message, Brushes.ForestGreen);
                break;
            case TelegramConnectionStatus.Disconnected:
                ShowDisconnectedLogin(state.Message);
                break;
        }
    }

    private void ShowDisconnectedLogin(string message)
    {
        LoggedInPanel.Visibility = Visibility.Collapsed;
        LoginFormPanel.Visibility = Visibility.Visible;
        LoginPromptLabel.Visibility = LoginValueBox.Visibility = LoginPasswordBox.Visibility =
            ContinueLoginButton.Visibility = Visibility.Collapsed;
        SetLoginBusy(false);
        SetStatus(message, Brushes.OrangeRed);
        if (!IsVisible && _trayIcon is not null)
            _trayIcon.ShowBalloonTip(
                5000,
                L("ConnectionErrorTitle"),
                L("ConnectionErrorBody"),
                System.Windows.Forms.ToolTipIcon.Error);
    }

    private async Task ActivateAccountAsync()
    {
        var userId = _telegram.CurrentUserId;
        if (userId == 0) throw new InvalidOperationException("无法取得当前 Telegram 账号 ID");
        if (!_settings.Accounts.TryGetValue(userId, out var account))
        {
            var isFirstAccount = _settings.Accounts.Count == 0;
            account = new AccountProfile
            {
                UserId = userId,
                DisplayName = _telegram.CurrentUser,
                PhoneNumber = _settings.PhoneNumber
            };
            if (isFirstAccount)
            {
                account.Schedules = _settings.Schedules;
                account.ExceptionAlerts = _settings.ExceptionAlerts;
                _settings.Schedules = [];
                _settings.ExceptionAlerts = new();
            }
            _settings.Accounts[userId] = account;
        }
        account.DisplayName = _telegram.CurrentUser;
        account.PhoneNumber = _settings.PhoneNumber;
        _activeAccount = account;
        _store.Save(_settings);

        _allDialogs = [];
        DialogsList.ItemsSource = null;
        ChatConsole.ClearOutput();
        MonitorConsole.ClearOutput();
        ConfirmationPeerBox.ItemsSource = null;
        ExceptionPeerBox.ItemsSource = null;
        ExceptionNotifyEnabledBox.IsChecked = account.ExceptionAlerts.NotificationsEnabled;
        ExceptionMinimumLevelBox.SelectedIndex = account.ExceptionAlerts.MinimumLevel == AppLogLevel.Critical ? 1 : 0;
        ExceptionEmailBox.Text = account.ExceptionAlerts.EmailRecipient;
        _exceptionMonitor.ActivateAccount(userId, account.ExceptionAlerts);
        MentionNotifyEnabledBox.IsChecked = account.MentionAlerts.NotificationsEnabled;
        _mentionMonitor.ActivateAccount(userId, account.MentionAlerts);
        _telegram.ConfigureAutomationRules(account.AutomationRules);
        await _scheduler.ActivateAccountAsync(account);
        RenderSchedules();
        await RefreshExceptionsAsync();
        await RefreshMentionsAsync();
        await RefreshOutboxAsync();
    }

    private async void LoginValueBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await RunUiAsync(ContinueLoginAsync);
    }

    private async void LoginPasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await RunUiAsync(ContinueLoginAsync);
    }

    private async void RefreshDialogs_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync(LoadDialogsAsync);

    private async Task LoadDialogsAsync()
    {
        _allDialogs = await _telegram.LoadDialogsAsync();
        FilterDialogs();
        var groups = _allDialogs.Where(x => x.IsGroup).ToList();
        ScheduleChatBox.ItemsSource = groups;
        if (groups.Count > 0) ScheduleChatBox.SelectedIndex = 0;
        var confirmationTargets = new List<ConfirmationTarget>
        {
            new(null, "", "（不发送 Telegram 确认）")
        };
        confirmationTargets.AddRange(_allDialogs.Select(x => new ConfirmationTarget(x, x.Kind, x.Name)));
        ConfirmationPeerBox.ItemsSource = confirmationTargets;
        ConfirmationPeerBox.SelectedIndex = 0;
        ExceptionPeerBox.ItemsSource = confirmationTargets;
        ExceptionPeerBox.SelectedItem = _activeAccount?.ExceptionAlerts.TelegramPeerId is long exceptionPeerId
            ? confirmationTargets.FirstOrDefault(x => x.Dialog?.Id == exceptionPeerId &&
                x.Kind == _activeAccount.ExceptionAlerts.TelegramPeerKind)
            : confirmationTargets[0];
        ExceptionPeerBox.SelectedItem ??= confirmationTargets[0];
        var mentionTargets = new List<ConfirmationTarget>
        {
            new(null, "", "（仅记录，不发送通知）")
        };
        mentionTargets.AddRange(_allDialogs.Where(x => !x.IsGroup)
            .Select(x => new ConfirmationTarget(x, x.Kind, x.Name)));
        MentionTargetBox.ItemsSource = mentionTargets;
        MentionTargetBox.SelectedItem = _activeAccount?.MentionAlerts.TargetPeerId is long mentionPeerId
            ? mentionTargets.FirstOrDefault(x => x.Dialog?.Id == mentionPeerId &&
                x.Kind == _activeAccount.MentionAlerts.TargetPeerKind)
            : mentionTargets[0];
        MentionTargetBox.SelectedItem ??= mentionTargets[0];
        SetStatus($"已加载 {_allDialogs.Count} 个会话，其中 {groups.Count} 个群聊/频道");
    }

    private void OpenConsole_Click(object sender, RoutedEventArgs e) => OpenSelectedConsole();

    private void DialogsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedConsole();

    private void OpenSelectedConsole()
    {
        if (DialogsList.SelectedItem is not DialogItem dialog)
        {
            ShowError(L("SelectDialogFirst"));
            return;
        }
        var window = new ChatConsoleWindow(_telegram, dialog);
        window.Show();
    }

    private void BlankConsole_Click(object sender, RoutedEventArgs e)
    {
        DialogsList.SelectedItem = null;
        ClearQuoteSelection();
        ChatConsole.ClearOutput();
        SetStatus(L("BlankStatus"));
    }

    private void ToggleDialogsButton_Click(object sender, RoutedEventArgs e)
    {
        if (DialogColumn.Width.Value > 0)
        {
            _expandedDialogWidth = DialogColumn.Width;
            DialogColumn.Width = new GridLength(0);
            ToggleDialogsButton.Content = "❯";
            ToggleDialogsButton.ToolTip = L("ExpandDialogs");
        }
        else
        {
            DialogColumn.Width = _expandedDialogWidth.Value > 0 ? _expandedDialogWidth : new GridLength(290);
            ToggleDialogsButton.Content = "❮";
            ToggleDialogsButton.ToolTip = L("CollapseDialogs");
        }
    }

    private void DialogFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialized) FilterDialogs();
    }

    private void FilterDialogs()
    {
        var selected = DialogsList.SelectedItem as DialogItem;
        var keyword = DialogFilterBox.Text.Trim();
        var items = string.IsNullOrEmpty(keyword)
            ? _allDialogs
            : _allDialogs.Where(x => x.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
        DialogsList.ItemsSource = null;
        DialogsList.ItemsSource = items;
        if (selected is not null)
            DialogsList.SelectedItem = items.FirstOrDefault(x => x.Id == selected.Id && x.Kind == selected.Kind);
    }

    private async void DialogsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingHistory || DialogsList.SelectedItem is not DialogItem dialog) return;
        await RunUiAsync(async () =>
        {
            _loadingHistory = true;
            try
            {
                ChatConsole.ClearOutput();
                ClearQuoteSelection();
                AppendConsole(ChatConsole, $"--- {dialog.Name} ---");
                foreach (var line in await _telegram.LoadHistoryAsync(dialog, QuoteHistoryLimit))
                    AppendChatLine(ChatConsole, line);
            }
            finally
            {
                _loadingHistory = false;
            }
        });
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync(SendMessageAsync);

    private void ClearQuoteButton_Click(object sender, RoutedEventArgs e) => ClearQuoteSelection();

    private async void MessageBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        await RunUiAsync(SendMessageAsync);
    }

    private async Task SendMessageAsync()
    {
        if (DialogsList.SelectedItem is not DialogItem dialog)
            throw new InvalidOperationException(L("SelectChatFirst"));
        var text = MessageBox.Text.Trim();
        if (text.Length == 0) return;
        SetChatSendEnabled(false);
        try
        {
            var quoteTarget = _selectedQuoteTarget;
            if (quoteTarget is null)
                await _telegram.SendAsync(dialog, text);
            else
                await _telegram.SendReplyAsync(dialog, quoteTarget.MessageId, text, quoteTarget.Text);
            MessageBox.Clear();
            SetStatus(quoteTarget is null ? LF("MessageSent", dialog.Name) : LF("QuoteReplySent", dialog.Name));
            ClearQuoteSelection();
        }
        finally
        {
            SetChatSendEnabled(true);
            MessageBox.Focus();
        }
    }

    private void ChatConsole_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _contextQuoteTarget = ChatConsole.GetTagAtVisualPosition<QuoteTargetItem>(
            e.GetPosition(ChatConsole.TextArea.TextView));
    }

    private void ChatConsole_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        ChatQuoteMenuItem.IsEnabled = _contextQuoteTarget is not null;
    }

    private void ChatQuoteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextQuoteTarget is null) return;
        SelectQuoteTarget(_contextQuoteTarget);
    }

    private void SelectQuoteTarget(QuoteTargetItem target)
    {
        _selectedQuoteTarget = target;
        QuotePreviewText.Text = $"↪ {target.DisplayText}";
        QuotePreviewPanel.Visibility = Visibility.Visible;
        MessageBox.Focus();
    }

    private void ClearQuoteSelection()
    {
        _selectedQuoteTarget = null;
        _contextQuoteTarget = null;
        QuotePreviewText.Text = string.Empty;
        QuotePreviewPanel.Visibility = Visibility.Collapsed;
    }

    private void SetChatSendEnabled(bool enabled)
    {
        MessageBox.IsEnabled = enabled;
        SendButton.IsEnabled = enabled;
    }

    private void HandleIncoming(ChatLine line)
    {
        if (_settings.MonitorEnabled && line.IsGroup) AppendChatLine(MonitorConsole, line);
        if (DialogsList.SelectedItem is DialogItem current && current.Id == line.ChatId)
            AppendChatLine(ChatConsole, line);
    }

    private void MonitorEnabledBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _settings.MonitorEnabled = MonitorEnabledBox.IsChecked == true;
        _store.Save(_settings);
    }

    private async void AddSchedule_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_activeAccount is null) throw new InvalidOperationException(L("LoginFirst"));
            if (ScheduleChatBox.SelectedItem is not DialogItem group)
                throw new InvalidOperationException(L("SelectTargetChat"));
            if (!TimeSpan.TryParseExact(ScheduleTimeBox.Text.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var time))
                throw new InvalidOperationException(L("InvalidTimeFormat"));
            if (time >= TimeSpan.FromDays(1)) throw new InvalidOperationException(L("InvalidTimeRange"));
            var message = ScheduleMessageBox.Text.Trim();
            if (message.Length == 0) throw new InvalidOperationException(L("MessageRequired"));
            var confirmationTarget = ConfirmationPeerBox.SelectedItem as ConfirmationTarget;
            var confirmationText = ConfirmationTextBox.Text.Trim();
            if (confirmationText.Length == 0) confirmationText = "签到完成：{群聊}，时间 {时间}";
            var confirmationEmail = ConfirmationEmailBox.Text.Trim();
            EnsureEmailConfigured(confirmationEmail);
            var period = SchedulePeriodBox.SelectedIndex == 1 ? SchedulePeriod.Weekly : SchedulePeriod.Daily;
            var weekDays = GetSelectedWeekDays();
            if (period == SchedulePeriod.Weekly && weekDays.Count == 0)
                throw new InvalidOperationException(L("WeeklyDayRequired"));

            var scheduledTask = new ScheduledMessage
            {
                ChatId = group.Id,
                ChatKind = group.Kind,
                ChatTitle = group.Name,
                Time = time,
                Period = period,
                WeekDays = weekDays,
                Message = message,
                ConfirmationPeerId = confirmationTarget?.Dialog?.Id,
                ConfirmationPeerKind = confirmationTarget?.Kind ?? "",
                ConfirmationPeerTitle = confirmationTarget?.Dialog?.Name ?? "",
                ConfirmationEmail = confirmationEmail,
                ConfirmationText = confirmationText
            };
            _activeAccount.Schedules.Add(scheduledTask);
            _store.Save(_settings);
            await _scheduler.UpsertAsync(scheduledTask);
            RenderSchedules();
            SetStatus(L("TaskAdded"));
        }
        catch (Exception ex)
        {
            if (ex is not InvalidOperationException) _logger.Error("UI", "界面操作失败", ex);
            ShowError(UserMessageFormatter.From(ex));
        }
    }

    private async void RemoveSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (_activeAccount is null) return;
        var selected = GetCheckedScheduleRows();
        if (selected.Count == 0)
        {
            ShowError(L("SelectTask"));
            return;
        }
        var selectedIds = selected.Select(x => x.Id).ToHashSet();
        _activeAccount.Schedules.RemoveAll(x => selectedIds.Contains(x.Id));
        _store.Save(_settings);
        foreach (var row in selected) await _scheduler.DeleteAsync(row.Id);
        RenderSchedules();
        SetStatus(LF("TasksDeleted", selected.Count));
    }

    private async void EditSchedule_Click(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(async () =>
        {
            var selected = GetCheckedScheduleRows();
            if (selected.Count != 1) throw new InvalidOperationException(L("EditOneTask"));
            var task = _activeAccount?.Schedules.FirstOrDefault(x => x.Id == selected[0].Id)
                ?? throw new InvalidOperationException(L("TaskNotFound"));
            var editor = new ScheduleEditWindow(task, _allDialogs, IsEmailConfigured()) { Owner = this };
            if (editor.ShowDialog() != true) return;
            _store.Save(_settings);
            await _scheduler.UpsertAsync(task);
            await _scheduler.RunDueTasksAsync();
            RenderSchedules();
            SetStatus($"定时任务已更新：{task.ChatTitle}，{DescribeSchedule(task)}");
        });
    }

    private async void RunNow_Click(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(async () =>
        {
            var selected = GetCheckedScheduleRows();
            if (selected.Count == 0) throw new InvalidOperationException(L("SelectTask"));
            var selectedIds = selected.Select(x => x.Id).ToHashSet();
            var emailRecipient = _activeAccount?.Schedules
                .FirstOrDefault(x => selectedIds.Contains(x.Id) && !string.IsNullOrWhiteSpace(x.ConfirmationEmail))
                ?.ConfirmationEmail;
            EnsureEmailConfigured(emailRecipient ?? "");
            RunNowButton.IsEnabled = false;
            try
            {
                SetStatus(LF("TasksRunning", selected.Count));
                var errors = new List<string>();
                foreach (var row in selected)
                {
                    try
                    {
                        await _scheduler.ExecuteNowAsync(row.Id);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{row.ChatTitle}: {UserMessageFormatter.From(ex)}");
                    }
                }
                RenderSchedules();
                if (errors.Count > 0)
                    throw new InvalidOperationException($"{selected.Count - errors.Count} 个成功，{errors.Count} 个失败：\n" + string.Join("\n", errors));
                SetStatus(LF("TasksCompleted", selected.Count));
            }
            finally
            {
                RunNowButton.IsEnabled = true;
            }
        });
    }

    private async void ScheduleList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ScheduleList.SelectedItem is not ScheduleRow row) return;
        var task = _activeAccount?.Schedules.FirstOrDefault(x => x.Id == row.Id);
        if (task is null) return;
        task.Enabled = !task.Enabled;
        _store.Save(_settings);
        await _scheduler.UpsertAsync(task);
        RenderSchedules();
    }

    private void RenderSchedules()
    {
        ScheduleList.ItemsSource = (_activeAccount?.Schedules ?? [])
            .OrderBy(x => x.Time)
            .Select(x => new ScheduleRow(
                x.Id,
                x.Enabled ? L("Enabled") : L("Disabled"),
                x.ChatTitle,
                DescribeSchedule(x),
                x.Message,
                BuildConfirmationSummary(x),
                x.LastSentDate?.ToString("yyyy-MM-dd") ?? "-"))
            .ToList();
    }

    private List<ScheduleRow> GetCheckedScheduleRows() =>
        ScheduleList.Items.Cast<ScheduleRow>().Where(x => x.IsChecked).ToList();

    private static string BuildConfirmationSummary(ScheduledMessage task)
    {
        var targets = new List<string>();
        if (task.ConfirmationPeerId is not null) targets.Add("TG: " + task.ConfirmationPeerTitle);
        if (!string.IsNullOrWhiteSpace(task.ConfirmationEmail)) targets.Add(L("EmailPrefix") + task.ConfirmationEmail);
        return targets.Count == 0 ? L("NoNotification") : string.Join(" / ", targets);
    }

    private bool IsEmailConfigured() =>
        !string.IsNullOrWhiteSpace(_settings.Email.SmtpHost)
        && _settings.Email.SmtpPort is >= 1 and <= 65535
        && !string.IsNullOrWhiteSpace(_settings.Email.FromAddress);

    private void EnsureEmailConfigured(string recipient)
    {
        if (!string.IsNullOrWhiteSpace(recipient) && !IsEmailConfigured())
            throw new InvalidOperationException(L("EmailNotConfigured"));
    }

    private void SchedulePeriodBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ScheduleWeekDaysPanel is not null)
            ScheduleWeekDaysPanel.IsEnabled = SchedulePeriodBox.SelectedIndex == 1;
    }

    private List<DayOfWeek> GetSelectedWeekDays()
    {
        var days = new List<DayOfWeek>();
        if (AddMonday.IsChecked == true) days.Add(DayOfWeek.Monday);
        if (AddTuesday.IsChecked == true) days.Add(DayOfWeek.Tuesday);
        if (AddWednesday.IsChecked == true) days.Add(DayOfWeek.Wednesday);
        if (AddThursday.IsChecked == true) days.Add(DayOfWeek.Thursday);
        if (AddFriday.IsChecked == true) days.Add(DayOfWeek.Friday);
        if (AddSaturday.IsChecked == true) days.Add(DayOfWeek.Saturday);
        if (AddSunday.IsChecked == true) days.Add(DayOfWeek.Sunday);
        return days;
    }

    private static string DescribeSchedule(ScheduledMessage task)
    {
        var time = task.Time.ToString(@"hh\:mm");
        if (task.Period == SchedulePeriod.Daily) return LF("DailySchedule", time);
        var names = new Dictionary<DayOfWeek, string>
        {
            [DayOfWeek.Monday] = L("MondayShort"), [DayOfWeek.Tuesday] = L("TuesdayShort"), [DayOfWeek.Wednesday] = L("WednesdayShort"),
            [DayOfWeek.Thursday] = L("ThursdayShort"), [DayOfWeek.Friday] = L("FridayShort"), [DayOfWeek.Saturday] = L("SaturdayShort"),
            [DayOfWeek.Sunday] = L("SundayShort")
        };
        var separator = LocalizationManager.CurrentLanguage == "en-US" ? ", " : "、";
        return LF("WeeklySchedule", string.Join(separator, task.WeekDays.Select(x => names[x])), time);
    }

    private async Task RunUiAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (InvalidOperationException ex)
        {
            var message = UserMessageFormatter.From(ex);
            SetLoginBusy(false);
            ContinueLoginButton.IsEnabled = true;
            SetStatus(LF("ErrorPrefix", message));
            ShowError(message);
        }
        catch (Exception ex)
        {
            _logger.Error("UI", "界面操作失败", ex);
            var message = UserMessageFormatter.From(ex);
            SetLoginBusy(false);
            ContinueLoginButton.IsEnabled = true;
            SetStatus(LF("ErrorPrefix", message));
            ShowError(message);
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_exitRequested)
        {
            e.Cancel = true;
            ShowInTaskbar = false;
            Hide();
            if (!_trayHintShown && _trayIcon is not null)
            {
                _trayHintShown = true;
                _trayIcon.ShowBalloonTip(
                    2500,
                    "Telegram 控制台助手仍在运行",
                    "定时任务和消息监控继续工作。双击托盘图标可恢复窗口。",
                    System.Windows.Forms.ToolTipIcon.Info);
            }
            return;
        }

        _logger.Info("Application", "应用正在退出");
        if (int.TryParse(ApiIdBox.Text, out var apiId)) _settings.ApiId = apiId;
        _settings.ApiHash = ApiHashBox.Password.Trim();
        _settings.PhoneNumber = PhoneBox.Text.Trim();
        _store.Save(_settings);
        _exceptionMonitor.RecordsChanged -= ExceptionMonitor_RecordsChanged;
        _exceptionMonitor.Dispose();
        _mentionMonitor.RecordsChanged -= MentionMonitor_RecordsChanged;
        _mentionMonitor.Dispose();
        _telegram.OutboxChanged -= Telegram_OutboxChanged;
        _telegram.AutomationActivity -= Telegram_AutomationActivity;
        _scheduler.Dispose();
        _telegram.Dispose();
        Application.Current.DispatcherUnhandledException -= Application_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
        _logger.EntryWritten -= Logger_EntryWritten;
        _logger.Dispose();
        DisposeTrayIcon();
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon is null) return;
        _trayIcon.Visible = false;
        _trayIcon.ContextMenuStrip?.Dispose();
        _trayIcon.Icon?.Dispose();
        _trayIcon.Dispose();
        _trayIcon = null;
    }

    private void Logger_EntryWritten(AppLogEntry entry) =>
        Dispatcher.BeginInvoke(() => AppendLogEntry(entry));

    private void AppendLogEntry(AppLogEntry entry)
    {
        var color = entry.Level switch
        {
            AppLogLevel.Warning => Brushes.Gold,
            AppLogLevel.Error or AppLogLevel.Critical => Brushes.OrangeRed,
            AppLogLevel.Debug or AppLogLevel.Trace => Brushes.Gray,
            _ => Brushes.White
        };
        var text = $"[{entry.Timestamp:HH:mm:ss.fff}] [{entry.Level}] [{entry.Category}] {entry.Message}";
        if (!string.IsNullOrWhiteSpace(entry.Exception)) text += Environment.NewLine + entry.Exception;
        AppendConsole(LogConsole, text, color);
    }

    private void OpenLogDirectory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_logger.LogDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.Error("UI", "无法打开日志目录", ex);
            ShowError(UserMessageFormatter.From(ex));
        }
    }

    private void ClearLogDisplay_Click(object sender, RoutedEventArgs e) => LogConsole.ClearOutput();

    private void Telegram_OutboxChanged() => Dispatcher.BeginInvoke(async () =>
    {
        try { await RefreshOutboxAsync(); }
        catch (Exception ex) { _logger.Error("Outbox", "刷新发件箱失败", ex); }
    });

    private void Telegram_AutomationActivity(string message) => Dispatcher.BeginInvoke(() =>
    {
        SetStatus(message);
        AppendConsole(MonitorConsole, $"[{DateTime.Now:HH:mm:ss}] [规则] {message}");
    });

    private async void RefreshOutbox_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync(RefreshOutboxAsync);

    private async Task RefreshOutboxAsync()
    {
        var records = await _telegram.QueryOutboxAsync();
        OutboxList.ItemsSource = records.Select(x => new OutboxRow(
            x.Id,
            x.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            x.Status,
            OutboxStatusText(x.Status),
            x.TargetTitle,
            OutboxPurposeText(x.Purpose),
            x.MessagePreview,
            x.AttemptCount,
            x.TelegramMessageId?.ToString() ?? "-",
            x.Error)).ToList();
    }

    private async void RetryOutbox_Click(object sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        var selected = OutboxList.SelectedItems.Cast<OutboxRow>().ToArray();
        if (selected.Length == 0) throw new InvalidOperationException(L("SelectOutboxRecord"));
        if (selected.Any(x => x.Status == OutgoingMessageStatus.Unknown) &&
            System.Windows.MessageBox.Show(
                L("ConfirmRetryUnknownOutbox"),
                L("ConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        foreach (var row in selected) await _telegram.RetryOutboxAsync(row.Id);
        await RefreshOutboxAsync();
        SetStatus(LF("OutboxRetryComplete", selected.Length));
    });

    private void OpenOutboxDatabase_Click(object sender, RoutedEventArgs e)
    {
        var directory = Path.GetDirectoryName(_telegram.OutboxDatabasePath)!;
        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    private static string OutboxStatusText(OutgoingMessageStatus status) => status switch
    {
        OutgoingMessageStatus.Queued => L("OutboxQueued"),
        OutgoingMessageStatus.Sending => L("OutboxSending"),
        OutgoingMessageStatus.Sent => L("OutboxSent"),
        OutgoingMessageStatus.Failed => L("OutboxFailed"),
        _ => L("OutboxUnknown")
    };

    private static string OutboxPurposeText(string purpose) => purpose switch
    {
        "Schedule" => L("OutboxSchedule"),
        "Confirmation" => L("OutboxConfirmation"),
        "Automation" => L("AutomationRules"),
        _ => L("OutboxManual")
    };

    private async void SaveMentionSettings_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync(() =>
        {
            SaveMentionSettings();
            SetStatus("@消息通知配置已加密保存");
            return Task.CompletedTask;
        });

    private void SaveMentionSettings()
    {
        if (_activeAccount is null) throw new InvalidOperationException("请先登录 Telegram 账号");
        var target = MentionTargetBox.SelectedItem as ConfirmationTarget;
        if (MentionNotifyEnabledBox.IsChecked == true && target?.Dialog is null)
            throw new InvalidOperationException("启用通知时请选择机器人或私聊");
        var settings = _activeAccount.MentionAlerts;
        settings.NotificationsEnabled = MentionNotifyEnabledBox.IsChecked == true;
        settings.TargetPeerId = target?.Dialog?.Id;
        settings.TargetPeerKind = target?.Kind ?? "";
        settings.TargetPeerTitle = target?.Dialog?.Name ?? "";
        _store.Save(_settings);
        _mentionMonitor.ActivateAccount(_activeAccount.UserId, settings);
    }

    private async void TestMentionNotification_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync(async () =>
        {
            SaveMentionSettings();
            await _mentionMonitor.SendTestNotificationAsync();
            SetStatus("@消息测试通知发送成功");
        });

    private async void RefreshMentions_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync(RefreshMentionsAsync);

    private async void ResetMentionQuery_Click(object sender, RoutedEventArgs e)
    {
        MentionKeywordBox.Clear();
        await RunUiAsync(RefreshMentionsAsync);
    }

    private async Task RefreshMentionsAsync()
    {
        var records = await _mentionMonitor.QueryAsync(new MentionQuery(MentionKeywordBox.Text.Trim()));
        MentionList.ItemsSource = records.Select(x => new MentionRow(
            x.Id,
            x.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            x.ChatName,
            x.Sender,
            x.Message,
            x.NotificationStatus)).ToList();
    }

    private void MentionMonitor_RecordsChanged() =>
        Dispatcher.BeginInvoke(async () =>
        {
            try { await RefreshMentionsAsync(); }
            catch (Exception ex) { _logger.Error("MentionMonitor", "刷新@消息列表失败", ex); }
        });

    private void MentionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MentionList.SelectedItem is not MentionRow row) return;
        System.Windows.MessageBox.Show(
            this,
            $"时间：{row.OccurredAtText}\n群聊：{row.ChatName}\n发送人：{row.Sender}\n通知：{row.NotificationStatus}\n\n{row.Message}",
            $"@我的消息 #{row.Id}",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OpenMentionDatabase_Click(object sender, RoutedEventArgs e)
    {
        var directory = Path.GetDirectoryName(_mentionMonitor.DatabasePath)!;
        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    private async void SaveExceptionSettings_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync(() =>
        {
            SaveExceptionSettings(true);
            return Task.CompletedTask;
        });

    private void SaveExceptionSettings(bool showStatus)
    {
        if (_activeAccount is null) throw new InvalidOperationException("请先登录 Telegram 账号");
        var target = ExceptionPeerBox.SelectedItem as ConfirmationTarget;
        var alerts = _activeAccount.ExceptionAlerts;
        alerts.NotificationsEnabled = ExceptionNotifyEnabledBox.IsChecked == true;
        alerts.MinimumLevel = ExceptionMinimumLevelBox.SelectedIndex == 1
            ? AppLogLevel.Critical
            : AppLogLevel.Error;
        alerts.TelegramPeerId = target?.Dialog?.Id;
        alerts.TelegramPeerKind = target?.Kind ?? "";
        alerts.TelegramPeerTitle = target?.Dialog?.Name ?? "";
        alerts.EmailRecipient = ExceptionEmailBox.Text.Trim();
        EnsureEmailConfigured(alerts.EmailRecipient);
        _store.Save(_settings);
        if (showStatus) SetStatus("异常通知配置已加密保存");
    }

    private async void TestExceptionNotification_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync(async () =>
        {
            SaveExceptionSettings(false);
            await _exceptionMonitor.SendTestNotificationAsync();
            SetStatus("异常测试通知发送成功");
        });

    private async void RefreshExceptions_Click(object sender, RoutedEventArgs e)
    {
        _exceptionQueryLimit = 500;
        await RunUiAsync(RefreshExceptionsAsync);
    }

    private async void RetryExceptionNotifications_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync(async () =>
        {
            SaveExceptionSettings(false);
            var ids = ExceptionList.SelectedItems.Cast<ExceptionRow>().Select(x => x.Id).ToList();
            if (ids.Count == 0) throw new InvalidOperationException("请先选择至少一条异常记录");
            await _exceptionMonitor.RetryNotificationsAsync(ids);
            await RefreshExceptionsAsync();
            SetStatus($"已重新处理 {ids.Count} 条异常通知");
        });

    private async Task RefreshExceptionsAsync()
    {
        var from = ToLocalDateOffset(ExceptionFromDatePicker.SelectedDate);
        var toExclusive = ToLocalDateOffset(ExceptionToDatePicker.SelectedDate?.AddDays(1));
        var level = ExceptionQueryLevelBox.SelectedIndex switch
        {
            1 => AppLogLevel.Error,
            2 => AppLogLevel.Critical,
            _ => (AppLogLevel?)null
        };
        var records = await _exceptionMonitor.QueryAsync(new ExceptionQuery(
            from, toExclusive, level, ExceptionKeywordBox.Text.Trim(), _exceptionQueryLimit));
        ExceptionList.ItemsSource = records.Select(x => new ExceptionRow(
            x.Id,
            x.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            x.Level.ToString(),
            x.Category,
            x.Message,
            x.Details,
            x.TelegramStatus,
            x.EmailStatus)).ToList();
        SetStatus(LF("ExceptionQueryComplete", records.Count));
    }

    private async void ResetExceptionQuery_Click(object sender, RoutedEventArgs e)
    {
        ExceptionFromDatePicker.SelectedDate = DateTime.Today;
        ExceptionToDatePicker.SelectedDate = DateTime.Today;
        ExceptionQueryLevelBox.SelectedIndex = 0;
        ExceptionKeywordBox.Clear();
        _exceptionQueryLimit = 10;
        await RunUiAsync(RefreshExceptionsAsync);
    }

    private static DateTimeOffset? ToLocalDateOffset(DateTime? date)
    {
        if (date is null) return null;
        var localDate = DateTime.SpecifyKind(date.Value.Date, DateTimeKind.Unspecified);
        return new DateTimeOffset(localDate, TimeZoneInfo.Local.GetUtcOffset(localDate));
    }

    private void ExceptionMonitor_RecordsChanged() =>
        Dispatcher.BeginInvoke(async () =>
        {
            try { await RefreshExceptionsAsync(); }
            catch (Exception ex) { _logger.Error("ExceptionMonitor", "刷新异常列表失败", ex); }
        });

    private void ExceptionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ExceptionList.SelectedItem is not ExceptionRow row) return;
        System.Windows.MessageBox.Show(
            this,
            $"时间：{row.OccurredAtText}\n级别：{row.Level}\n来源：{row.Category}\n\n{row.Message}\n\n{row.Details}",
            $"异常详情 #{row.Id}",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void OpenExceptionDatabase_Click(object sender, RoutedEventArgs e)
    {
        var directory = Path.GetDirectoryName(_exceptionMonitor.DatabasePath)!;
        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    private void Application_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e) =>
        _logger.Write(AppLogLevel.Critical, "Application", "发生未处理的界面异常", e.Exception);

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) =>
        _logger.Write(AppLogLevel.Error, "Application", "发生未观察的后台任务异常", e.Exception);

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            _logger.Write(AppLogLevel.Critical, "Application", "发生未处理的进程异常", exception);
    }

    private void SetLoginBusy(bool busy)
    {
        LoginButton.IsEnabled = !busy;
        ApiIdBox.IsEnabled = ApiHashBox.IsEnabled = PhoneBox.IsEnabled = !busy;
    }

    private void SetStatus(string text, Brush? color = null)
    {
        StatusText.Text = $"[{DateTime.Now:HH:mm:ss}] {text}";
        StatusText.Foreground = color ?? Brushes.DarkSlateGray;
        StatusText.ScrollToEnd();
    }

    private void ShowError(string text) => System.Windows.MessageBox.Show(
        this, text, L("OperationFailed"), MessageBoxButton.OK, MessageBoxImage.Error);

    private static string PromptText(string prompt) => prompt switch
    {
        "verification_code" => L("VerificationCode"),
        "password" => L("PromptPassword"),
        "name" => L("PromptName"),
        "email" => L("PromptEmail"),
        "email_verification_code" => L("PromptEmailCode"),
        _ => prompt
    };

    private static void AppendChatLine(BufferedTerminal box, ChatLine line) =>
        AppendConsole(
            box,
            $"[{line.Time:HH:mm:ss}] [{line.Chat}] {line.Sender}: {line.Text}",
            line.IsMentioned ? Brushes.DodgerBlue : line.IsOutgoing ? Brushes.LimeGreen : Brushes.White,
            line.MessageId > 0 ? QuoteTargetItem.From(line) : null,
            line.MessageId > 0 ? (line.ChatKind, line.ChatId, line.MessageId, line.Text) : null);

    private static void AppendConsole(
        BufferedTerminal box,
        string text,
        Brush? color = null,
        object? tag = null,
        object? deduplicationKey = null) =>
        box.AppendLine(text, color ?? Brushes.White, tag, deduplicationKey);

    private sealed class ScheduleRow(
        Guid id,
        string enabledText,
        string chatTitle,
        string periodText,
        string message,
        string confirmation,
        string lastSentText)
    {
        public bool IsChecked { get; set; }
        public Guid Id { get; } = id;
        public string EnabledText { get; } = enabledText;
        public string ChatTitle { get; } = chatTitle;
        public string PeriodText { get; } = periodText;
        public string Message { get; } = message;
        public string Confirmation { get; } = confirmation;
        public string LastSentText { get; } = lastSentText;
    }

    private static string L(string key) => LocalizationManager.Text(key);
    private static string LF(string key, params object?[] args) => LocalizationManager.Format(key, args);

    private sealed record ConfirmationTarget(DialogItem? Dialog, string Kind, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record MentionRow(
        long Id,
        string OccurredAtText,
        string ChatName,
        string Sender,
        string Message,
        string NotificationStatus);

    private sealed record ExceptionRow(
        long Id,
        string OccurredAtText,
        string Level,
        string Category,
        string Message,
        string Details,
        string TelegramStatus,
        string EmailStatus);

    private sealed record OutboxRow(
        long Id,
        string CreatedAtText,
        OutgoingMessageStatus Status,
        string StatusText,
        string TargetTitle,
        string PurposeText,
        string MessagePreview,
        int AttemptCount,
        string TelegramMessageId,
        string Error);
}
