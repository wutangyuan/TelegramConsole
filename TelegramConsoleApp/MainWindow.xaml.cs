using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;

namespace TelegramConsoleApp;

public partial class MainWindow : Window
{
    private const int QuoteHistoryLimit = 300;
    private readonly ISettingsStore _store = new SettingsStore();
    private readonly IAppLogger _logger = new Log4NetAppLogger();
    private readonly AppSettings _settings;
    private readonly AccountLaunchRequest? _launchRequest;
    private readonly bool _managedWorkspace;
    private readonly ITelegramService _telegram;
    private readonly ISchedulerService _scheduler;
    private readonly IIntervalChatAutomationService _intervalChatAutomation;
    private readonly IExceptionMonitorService _exceptionMonitor;
    private readonly IMentionMonitorService _mentionMonitor;
    private AccountProfile? _activeAccount;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Windows.Forms.ToolStripMenuItem? _trayAccountItem;
    private bool _exitRequested;
    private bool _trayHintShown;
    private List<DialogItem> _allDialogs = [];
    private readonly List<ChatLine> _chatTimeline = [];
    private readonly Dictionary<(string ChatKind, long ChatId, int MessageId), CachedDeletion> _chatDeletions = [];
    private QuoteTargetItem? _selectedQuoteTarget;
    private QuoteTargetItem? _contextQuoteTarget;
    private int? _editingMessageId;
    private ChatLine? _editingMessage;
    private bool _loadingHistory;
    private bool _initialized;
    private bool _resourcesDisposed;
    private bool _loginInProgress;
    private bool _workspaceConnectionFailed;
    private bool _manualLoginEmailSent;
    private Task? _workspaceStartTask;
    private (string Kind, long Id, DateTime ExpiresUtc)? _pendingSentScroll;
    private bool _unreadNotificationsReady;
    private bool _outboxBaselineReady;
    private bool _mentionBaselineReady;
    private bool _exceptionBaselineReady;
    private HashSet<long> _knownOutboxIds = [];
    private HashSet<long> _knownMentionIds = [];
    private HashSet<long> _knownExceptionIds = [];
    private int _exceptionQueryLimit = 10;
    private GridLength _expandedDialogWidth = new(320);
    private ChatPresentationMode _chatPresentationMode;

    public long AccountUserId => _activeAccount?.UserId ?? 0;
    public string WorkspaceDisplayName => _activeAccount?.LocalName is { Length: > 0 } localName
        ? localName
        : _activeAccount?.DisplayName ?? _launchRequest?.LocalName ?? "新账户";
    public string WorkspaceAccountLabel => _activeAccount is null
        ? WorkspaceDisplayName
        : string.IsNullOrWhiteSpace(_activeAccount.LocalName) || _activeAccount.LocalName == _activeAccount.DisplayName
            ? _activeAccount.DisplayName
            : $"{_activeAccount.LocalName} · {_activeAccount.DisplayName}";
    public bool IsWorkspaceOnline => _telegram.IsLoggedIn;
    public bool IsWorkspaceConnectionFailed => _workspaceConnectionFailed;
    public string PhoneNumber => PhoneBox.Text.Trim();

    public MainWindow() : this(null, false)
    {
    }

    internal MainWindow(AccountLaunchRequest? launchRequest, bool managedWorkspace)
    {
        _launchRequest = launchRequest;
        _managedWorkspace = managedWorkspace;
        InitializeComponent();
        if (!_managedWorkspace) InitializeTrayIcon();
        _settings = _store.Load();
        var knownAccount = _settings.Accounts.Values.FirstOrDefault(x =>
            SamePhone(x.PhoneNumber, launchRequest?.PhoneNumber ?? _settings.PhoneNumber));
        var loginLocalName = !string.IsNullOrWhiteSpace(launchRequest?.LocalName)
            ? launchRequest.LocalName
            : !string.IsNullOrWhiteSpace(knownAccount?.LocalName)
                ? knownAccount.LocalName
                : !string.IsNullOrWhiteSpace(knownAccount?.DisplayName)
                    ? knownAccount.DisplayName
                    : "新账户";
        var loginDisplayName = knownAccount?.DisplayName ?? "";
        UpdateLoginIdentity(loginLocalName, loginDisplayName);
        _telegram = new TelegramService(_store, _logger);
        _exceptionMonitor = new ExceptionMonitorService(_store, _logger, _telegram, _settings);
        _mentionMonitor = new MentionMonitorService(_store, _telegram, _logger);
        _scheduler = new SchedulerService(_telegram, _store, _settings, _logger);
        _intervalChatAutomation = new IntervalChatAutomationService(_telegram, _store, _logger);
        VisualChat.QuoteRequested += line => SelectQuoteTarget(QuoteTargetItem.From(line));
        VisualChat.MediaOpenRequested += VisualChat_MediaOpenRequested;
        VisualChat.PreviewRequested += VisualChat_PreviewRequested;
        VisualChat.EditRequested += BeginEditMessage;
        VisualChat.DeleteRequested += VisualChat_DeleteRequested;
        VisualChat.CopyTextRequested += VisualChat_CopyTextRequested;
        VisualChat.CopyLinkRequested += VisualChat_CopyLinkRequested;
        VisualChat.CopyMediaRequested += VisualChat_CopyMediaRequested;
        VisualChat.ForwardRequested += VisualChat_ForwardRequested;
        VisualChat.ReactionRequested += VisualChat_ReactionRequested;
        _chatPresentationMode = Enum.TryParse<ChatPresentationMode>(_settings.ChatViewMode, true, out var mode)
            ? mode
            : ChatPresentationMode.Console;
        ApplyChatPresentationMode();
        RuntimeLogsTab.Visibility = Visibility.Collapsed;

        ApiIdBox.Text = _settings.ApiId == 0 ? "" : _settings.ApiId.ToString();
        ApiHashBox.Password = _settings.ApiHash;
        PhoneBox.Text = launchRequest?.PhoneNumber ?? _settings.PhoneNumber;
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
        _telegram.MessageDeleted += Telegram_MessageDeleted;
        _telegram.Log += text => Dispatcher.BeginInvoke(() => SetStatus(text));
        _telegram.ConnectionStateChanged += state => Dispatcher.BeginInvoke(() => HandleConnectionState(state));
        _telegram.OutboxChanged += Telegram_OutboxChanged;
        _telegram.AutomationActivity += Telegram_AutomationActivity;
        _scheduler.Status += text =>
        {
            var notifyUnread = _unreadNotificationsReady;
            Dispatcher.BeginInvoke(() =>
            {
                SetStatus(text);
                AppendConsole(MonitorConsole, $"[{DateTime.Now:HH:mm:ss}] [任务] {text}");
                if (notifyUnread) MarkTabUnread(MonitorTab);
            });
        };
        _intervalChatAutomation.Status += text =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                SetStatus(text);
                RenderIntervalChatRules();
            });
        };
        _exceptionMonitor.RecordsChanged += ExceptionMonitor_RecordsChanged;
        _mentionMonitor.RecordsChanged += MentionMonitor_RecordsChanged;
        if (!_managedWorkspace)
        {
            Application.Current.DispatcherUnhandledException += Application_DispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        }
        _logger.Info("Application", "WPF 主窗口已初始化");
        Closing += MainWindow_Closing;
        Loaded += MainWindow_Loaded;
        Closed += (_, _) => AccountWorkspaceManager.Unregister(this);
        if (loginLocalName != "新账户") Title = $"Telegram 控制台助手 - {loginLocalName}";
        _initialized = true;
        ClearAllUnreadIndicators();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(EnsureWorkspaceStartedAsync);
    }

    private async Task EnsureWorkspaceStartedAsync()
    {
        if (_workspaceStartTask is null || _workspaceStartTask.IsFaulted || _workspaceStartTask.IsCanceled)
            _workspaceStartTask = InitializeWorkspaceAsync();
        await _workspaceStartTask;
    }

    private async Task InitializeWorkspaceAsync()
    {
        await RefreshExceptionsAsync();
        if (_launchRequest?.AutoLogin == true && !_telegram.IsLoggedIn)
            await BeginLoginAsync();
    }

    internal async void StartWorkspaceInBackground() =>
        await RunUiAsync(EnsureWorkspaceStartedAsync);

    private void InitializeTrayIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/AppIcon.ico"))
            ?? throw new InvalidOperationException("找不到应用图标资源");
        using var sourceIcon = new System.Drawing.Icon(resource.Stream);
        var trayMenu = new System.Windows.Forms.ContextMenuStrip();
        _trayAccountItem = new System.Windows.Forms.ToolStripMenuItem
        {
            Enabled = false,
            Text = LF("TrayCurrentAccount", L("NotLoggedIn")),
            Font = new System.Drawing.Font(trayMenu.Font, System.Drawing.FontStyle.Bold)
        };
        trayMenu.Items.Add(_trayAccountItem);
        trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
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
        AccountWorkspaceManager.Changed += UpdateTrayWorkspaces;
        UpdateTrayAccount();
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
        AccountWorkspaceManager.StopAll(this);
        Application.Current.Shutdown();
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

    private void AccountManager_Click(object sender, RoutedEventArgs e)
    {
        ((App)System.Windows.Application.Current).ShowManagementCenter();
    }

    private void ToggleLoginPanel_Click(object sender, RoutedEventArgs e)
    {
        if (_telegram.IsLoggedIn)
        {
            ((App)System.Windows.Application.Current).ShowManagementCenter();
            return;
        }

        ConnectionSettingsGroup.Visibility = ConnectionSettingsGroup.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (ConnectionSettingsGroup.Visibility == Visibility.Visible)
            (string.IsNullOrWhiteSpace(LoginAliasBox.Text) ? LoginAliasBox : PhoneBox).Focus();
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
        if (_loginInProgress) return;
        _loginInProgress = true;
        _workspaceConnectionFailed = false;
        AccountWorkspaceManager.NotifyChanged();
        try
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
        catch (Exception ex)
        {
            _workspaceConnectionFailed = true;
            ShowDisconnectedLogin(UserMessageFormatter.From(ex));
            AccountWorkspaceManager.NotifyChanged();
            throw;
        }
        finally
        {
            _loginInProgress = false;
        }
    }

    internal async void StartLoginFromManager(bool activate = true)
    {
        if (activate) ShowWorkspace();
        await RunUiAsync(async () =>
        {
            await EnsureWorkspaceStartedAsync();
            // Auto-login is already performed by InitializeWorkspaceAsync. Do not
            // restart it after a verification prompt or during recovery.
            if (_launchRequest?.AutoLogin != true && !_telegram.IsLoggedIn && !_loginInProgress)
                await BeginLoginAsync();
        });
    }

    private async Task DeactivateAccountAsync()
    {
        _unreadNotificationsReady = false;
        ResetUnreadBaselines();
        ClearAllUnreadIndicators();
        _activeAccount = null;
        UpdateTrayAccount();
        _exceptionMonitor.DeactivateAccount();
        _mentionMonitor.DeactivateAccount();
        _telegram.ConfigureAutomationRules([]);
        await _scheduler.DeactivateAccountAsync();
        await _intervalChatAutomation.DeactivateAccountAsync();
        _allDialogs = [];
        DialogsList.ItemsSource = null;
        ScheduleChatBox.ItemsSource = null;
        IntervalSourceBox.ItemsSource = null;
        IntervalTargetBox.ItemsSource = null;
        ConfirmationPeerBox.ItemsSource = null;
        ExceptionPeerBox.ItemsSource = null;
        MentionTargetBox.ItemsSource = null;
        ExceptionNotifyEnabledBox.IsChecked = false;
        ExceptionMinimumLevelBox.SelectedIndex = 0;
        ExceptionEmailBox.Clear();
        MentionNotifyEnabledBox.IsChecked = false;
        ScheduleList.ItemsSource = null;
        IntervalRuleList.ItemsSource = null;
        ExceptionList.ItemsSource = null;
        MentionList.ItemsSource = null;
        OutboxList.ItemsSource = null;
        ClearQuoteSelection();
        _chatTimeline.Clear();
        _chatDeletions.Clear();
        ChatConsole.ClearOutput();
        VisualChat.ClearMessages();
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
            ConnectionSettingsGroup.Visibility = Visibility.Visible;
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
        _manualLoginEmailSent = false;
        _workspaceConnectionFailed = false;
        LoggedInAccountText.Text = _telegram.CurrentUser;
        UpdateTrayAccount(_telegram.CurrentUser);
        ConnectionStatusText.Text = L("Online");
        ConnectionStatusText.Foreground = Brushes.ForestGreen;
        ConnectionStatusDot.Background = Brushes.LimeGreen;
        LoginFormPanel.Visibility = Visibility.Collapsed;
        LoggedInPanel.Visibility = Visibility.Visible;
        LoginButton.IsEnabled = false;
        ConnectionSettingsGroup.Visibility = Visibility.Collapsed;
        TopConnectionDot.Background = Brushes.LimeGreen;
        TopConnectionText.Text = L("Online");
        TopConnectionText.Foreground = Brushes.ForestGreen;
        TopLoginButton.Visibility = Visibility.Collapsed;
    }

    private void HandleConnectionState(TelegramConnectionState state)
    {
        AccountWorkspaceManager.NotifyChanged();
        switch (state.Status)
        {
            case TelegramConnectionStatus.Connecting:
                TopConnectionDot.Background = Brushes.DarkOrange;
                TopConnectionText.Text = L("ConnectingStatus");
                TopConnectionText.Foreground = Brushes.DarkOrange;
                if (LoggedInPanel.Visibility == Visibility.Visible)
                {
                    ConnectionStatusText.Text = L("ConnectingStatus");
                    ConnectionStatusText.Foreground = Brushes.DarkOrange;
                    ConnectionStatusDot.Background = Brushes.DarkOrange;
                }
                SetStatus(state.Message, Brushes.DarkOrange);
                break;
            case TelegramConnectionStatus.Recovering:
                TopConnectionDot.Background = Brushes.DarkOrange;
                TopConnectionText.Text = L("RecoveringStatus");
                TopConnectionText.Foreground = Brushes.DarkOrange;
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
                _workspaceConnectionFailed = false;
                if (_telegram.CurrentUserId != 0) ShowAuthenticatedAccount();
                SetStatus(state.Message, Brushes.ForestGreen);
                break;
            case TelegramConnectionStatus.Disconnected:
                _workspaceConnectionFailed = true;
                ShowDisconnectedLogin(state.Message);
                break;
        }
    }

    private void ShowDisconnectedLogin(string message)
    {
        UpdateTrayAccount();
        LoggedInPanel.Visibility = Visibility.Collapsed;
        LoginFormPanel.Visibility = Visibility.Visible;
        ConnectionSettingsGroup.Visibility = Visibility.Visible;
        TopConnectionDot.Background = Brushes.OrangeRed;
        TopConnectionText.Text = "需要重新登录";
        TopConnectionText.Foreground = Brushes.OrangeRed;
        TopLoginButton.Visibility = Visibility.Visible;
        TopLoginButton.Content = "登录 / 添加账户";
        LoginPromptLabel.Visibility = LoginValueBox.Visibility = LoginPasswordBox.Visibility =
            ContinueLoginButton.Visibility = Visibility.Collapsed;
        SetLoginBusy(false);
        SetStatus(message, Brushes.OrangeRed);
        _ = SendManualLoginRequiredEmailAsync(message);
        if (!IsVisible && _trayIcon is not null)
            _trayIcon.ShowBalloonTip(
                5000,
                L("ConnectionErrorTitle"),
                L("ConnectionErrorBody"),
                System.Windows.Forms.ToolTipIcon.Error);
    }

    private async Task SendManualLoginRequiredEmailAsync(string reason)
    {
        if (_manualLoginEmailSent || _activeAccount is null || !_activeAccount.ExceptionAlerts.ManualLoginEmailReminderEnabled) return;
        var recipient = _activeAccount.ExceptionAlerts.EmailRecipient.Trim();
        if (string.IsNullOrWhiteSpace(recipient) || !IsEmailConfigured()) return;

        _manualLoginEmailSent = true;
        var account = string.IsNullOrWhiteSpace(_activeAccount.DisplayName)
            ? _activeAccount.LocalName
            : _activeAccount.DisplayName;
        var body = $"【Telegram 控制台登录提醒】\n账户：{account}\n时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n状态：连接已确认无法自动恢复，需要手动重新登录。\n原因：{reason}";
        try
        {
            await EmailNotificationService.SendAsync(_settings.Email, recipient, "Telegram 账户需要重新登录", body);
            _logger.Info("Telegram.Connection", $"已向 {recipient} 发送账户重新登录邮件提醒");
        }
        catch (Exception ex)
        {
            _logger.Warning("Telegram.Connection", "账户重新登录邮件提醒发送失败", ex);
        }
    }

    private void UpdateTrayAccount(string? accountName = null)
    {
        if (_trayAccountItem is null) return;
        var displayName = string.IsNullOrWhiteSpace(accountName) ? L("NotLoggedIn") : accountName;
        _trayAccountItem.Text = LF("TrayCurrentAccount", displayName);
        if (_trayIcon is not null)
        {
            var text = $"Telegram - {displayName}";
            _trayIcon.Text = text.Length <= 63 ? text : text[..63];
        }
        UpdateTrayWorkspaces();
    }

    private void UpdateTrayWorkspaces()
    {
        if (_trayAccountItem is null) return;
        var workspaces = AccountWorkspaceManager.RunningWorkspaces();
        _trayAccountItem.DropDownItems.Clear();
        _trayAccountItem.Enabled = workspaces.Count > 0;
        _trayAccountItem.Text = workspaces.Count == 0 ? L("NotLoggedIn") : $"运行中的账户：{workspaces.Count}";
        foreach (var workspace in workspaces)
        {
            var item = new System.Windows.Forms.ToolStripMenuItem
            {
                Text = $"{(workspace.IsOnline ? "●" : "○")} {workspace.DisplayName}",
                Tag = workspace.Window
            };
            item.Click += (_, _) => Dispatcher.BeginInvoke(workspace.Window.ShowWorkspace);
            _trayAccountItem.DropDownItems.Add(item);
        }
        if (_trayIcon is not null)
        {
            var online = workspaces.Count(x => x.IsOnline);
            var text = $"Telegram - {online}/{workspaces.Count} 在线";
            _trayIcon.Text = text.Length <= 63 ? text : text[..63];
        }
    }

    private async Task ActivateAccountAsync()
    {
        _unreadNotificationsReady = false;
        ResetUnreadBaselines();
        ClearAllUnreadIndicators();
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
        var localName = LoginAliasBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(localName)) account.LocalName = localName;
        else if (string.IsNullOrWhiteSpace(account.LocalName))
            account.LocalName = string.IsNullOrWhiteSpace(_launchRequest?.LocalName) ? account.DisplayName : _launchRequest.LocalName;
        _activeAccount = account;
        _store.SaveAccount(account);
        _store.Save(_settings);

        _allDialogs = [];
        DialogsList.ItemsSource = null;
        _chatTimeline.Clear();
        _chatDeletions.Clear();
        ChatConsole.ClearOutput();
        VisualChat.ClearMessages();
        MonitorConsole.ClearOutput();
        ConfirmationPeerBox.ItemsSource = null;
        ExceptionPeerBox.ItemsSource = null;
        ExceptionNotifyEnabledBox.IsChecked = account.ExceptionAlerts.NotificationsEnabled;
        ExceptionEmailNotifyEnabledBox.IsChecked = account.ExceptionAlerts.EmailNotificationsEnabled;
        ManualLoginEmailReminderBox.IsChecked = account.ExceptionAlerts.ManualLoginEmailReminderEnabled;
        ExceptionMinimumLevelBox.SelectedIndex = account.ExceptionAlerts.MinimumLevel == AppLogLevel.Critical ? 1 : 0;
        ExceptionEmailBox.Text = account.ExceptionAlerts.EmailRecipient;
        _exceptionMonitor.ActivateAccount(userId, account.ExceptionAlerts);
        MentionNotifyEnabledBox.IsChecked = account.MentionAlerts.NotificationsEnabled;
        _mentionMonitor.ActivateAccount(userId, account.MentionAlerts);
        _telegram.ConfigureAutomationRules(account.AutomationRules);
        await _scheduler.ActivateAccountAsync(account);
        await _intervalChatAutomation.ActivateAccountAsync(account);
        RenderSchedules();
        RenderIntervalChatRules();
        await RefreshExceptionsAsync();
        await RefreshMentionsAsync();
        await RefreshOutboxAsync();
        ClearAllUnreadIndicators();
        _unreadNotificationsReady = true;
        Title = $"Telegram 控制台助手 - {account.LocalName} ({account.DisplayName})";
        UpdateLoginIdentity(account.LocalName, account.DisplayName);
        AccountWorkspaceManager.Register(this, userId);
    }

    private void UpdateLoginIdentity(string localName, string displayName)
    {
        var identity = string.IsNullOrWhiteSpace(displayName) || displayName == localName
            ? localName
            : $"{localName} · {displayName}";
        LoginAccountHint.Text = $"登录：{identity}";
        LoginAliasBox.Text = localName == "新账户" ? "" : localName;
        ConnectionSettingsGroup.Header = $"账户登录 · {identity}";
        TopAccountText.Text = identity;
    }

    private static bool SamePhone(string left, string right) =>
        !string.IsNullOrWhiteSpace(left) &&
        new string(left.Where(char.IsDigit).ToArray()) == new string(right.Where(char.IsDigit).ToArray());

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
        DialogTypeFilterBox.ItemsSource = new List<DialogTypeFilter>
        {
            new DialogTypeFilter(null, L("DialogTypeAll")),
            new DialogTypeFilter(DialogCategory.Private, L("DialogTypePrivate")),
            new DialogTypeFilter(DialogCategory.Bot, L("DialogTypeBot")),
            new DialogTypeFilter(DialogCategory.Group, L("DialogTypeGroup")),
            new DialogTypeFilter(DialogCategory.Channel, L("DialogTypeChannel"))
        };
        DialogTypeFilterBox.SelectedIndex = 0;
        FilterDialogs();
        var groups = _allDialogs.Where(x => x.IsGroup).ToList();
        ScheduleChatBox.ItemsSource = groups;
        if (groups.Count > 0) ScheduleChatBox.SelectedIndex = 0;
        IntervalSourceBox.ItemsSource = _allDialogs;
        IntervalTargetBox.ItemsSource = _allDialogs;
        if (_allDialogs.Count > 0)
        {
            IntervalSourceBox.SelectedIndex = 0;
            IntervalTargetBox.SelectedIndex = 0;
        }
        var confirmationTargets = new List<ConfirmationTarget>
        {
            new(null, "", "（不发送 Telegram 确认）")
        };
        confirmationTargets.AddRange(_allDialogs.Select(x => new ConfirmationTarget(x, x.Kind, x.DisplayName)));
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
            .Select(x => new ConfirmationTarget(x, x.Kind, x.DisplayName)));
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
        var window = new ChatConsoleWindow(_telegram, dialog, this, WorkspaceAccountLabel);
        window.Show();
    }

    private void BlankConsole_Click(object sender, RoutedEventArgs e)
    {
        DialogsList.SelectedItem = null;
        ClearQuoteSelection();
        _chatTimeline.Clear();
        ChatConsole.ClearOutput();
        VisualChat.ClearMessages();
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
            DialogColumn.Width = _expandedDialogWidth.Value > 0 ? _expandedDialogWidth : new GridLength(320);
            ToggleDialogsButton.Content = "❮";
            ToggleDialogsButton.ToolTip = L("CollapseDialogs");
        }
    }

    private void DialogFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialized) FilterDialogs();
    }

    private void DialogTypeFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized) FilterDialogs();
    }

    private void FilterDialogs()
    {
        var selected = DialogsList.SelectedItem as DialogItem;
        var keyword = DialogFilterBox.Text.Trim();
        var selectedType = (DialogTypeFilterBox.SelectedItem as DialogTypeFilter)?.Category;
        var items = _allDialogs
            .Where(x => selectedType is null || MatchesDialogCategory(x, selectedType.Value))
            .Where(x => string.IsNullOrEmpty(keyword) || x.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .ToList();
        DialogsList.ItemsSource = null;
        DialogsList.ItemsSource = items;
        if (selected is not null)
            DialogsList.SelectedItem = items.FirstOrDefault(x => x.Id == selected.Id && x.Kind == selected.Kind);
    }

    private static bool MatchesDialogCategory(DialogItem dialog, DialogCategory category) => category switch
    {
        DialogCategory.Group => dialog.EffectiveCategory is DialogCategory.Group or DialogCategory.Supergroup,
        _ => dialog.EffectiveCategory == category
    };

    private async void DialogsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingHistory || DialogsList.SelectedItem is not DialogItem dialog) return;
        await RunUiAsync(async () =>
        {
            _loadingHistory = true;
            try
            {
                ClearQuoteSelection();
                var history = await _telegram.LoadHistoryAsync(dialog, QuoteHistoryLimit);
                var availableReactions = await _telegram.LoadAvailableReactionsAsync(dialog);
                _chatTimeline.Clear();
                _chatTimeline.AddRange(history.OrderBy(x => x.Time).ThenBy(x => x.MessageId));
                RenderChatTimeline(dialog, forceScrollToEnd: true);
                VisualChat.ReplaceMessages(history);
                VisualChat.SetAvailableReactions(availableReactions);
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

    private void ConsoleViewButton_Click(object sender, RoutedEventArgs e) => SetChatPresentationMode(ChatPresentationMode.Console);
    private void VisualViewButton_Click(object sender, RoutedEventArgs e) => SetChatPresentationMode(ChatPresentationMode.Visual);
    private void SplitViewButton_Click(object sender, RoutedEventArgs e) => SetChatPresentationMode(ChatPresentationMode.Split);

    private void SetChatPresentationMode(ChatPresentationMode mode)
    {
        _chatPresentationMode = mode;
        _settings.ChatViewMode = mode.ToString();
        _store.Save(_settings);
        ApplyChatPresentationMode();
    }

    private void ApplyChatPresentationMode()
    {
        var showConsole = _chatPresentationMode is ChatPresentationMode.Console or ChatPresentationMode.Split;
        var showVisual = _chatPresentationMode is ChatPresentationMode.Visual or ChatPresentationMode.Split;
        ChatConsole.Visibility = showConsole ? Visibility.Visible : Visibility.Collapsed;
        VisualChat.Visibility = showVisual ? Visibility.Visible : Visibility.Collapsed;
        ChatViewSplitter.Visibility = _chatPresentationMode == ChatPresentationMode.Split ? Visibility.Visible : Visibility.Collapsed;
        ConsoleViewColumn.Width = showConsole ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ChatViewSplitterColumn.Width = _chatPresentationMode == ChatPresentationMode.Split ? new GridLength(6) : new GridLength(0);
        VisualViewColumn.Width = showVisual ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ConsoleViewButton.IsChecked = _chatPresentationMode == ChatPresentationMode.Console;
        VisualViewButton.IsChecked = _chatPresentationMode == ChatPresentationMode.Visual;
        SplitViewButton.IsChecked = _chatPresentationMode == ChatPresentationMode.Split;
    }

    private async void VisualChat_MediaOpenRequested(ChatLine line)
    {
        if (DialogsList.SelectedItem is not DialogItem dialog || dialog.Id != line.ChatId) return;
        await RunUiAsync(async () =>
        {
            SetStatus(LF("DownloadingMedia", line.MediaLabel));
            var path = await _telegram.DownloadMediaAsync(dialog, line.MessageId);
            if (MediaFileLauncher.Open(this, path)) SetStatus(LF("MediaOpened", Path.GetFileName(path)));
        });
    }

    private async void VisualChat_CopyMediaRequested(ChatLine line)
    {
        if (DialogsList.SelectedItem is not DialogItem dialog || dialog.Id != line.ChatId) return;
        await RunUiAsync(async () =>
        {
            SetStatus(LF("DownloadingMedia", line.MediaLabel));
            var path = await _telegram.DownloadMediaAsync(dialog, line.MessageId);
            var copyAsImage = line.Media?.Kind is ChatMediaKind.Photo or ChatMediaKind.Sticker;
            if (!await ClipboardHelper.TrySetMediaAsync(path, copyAsImage))
                throw new InvalidOperationException("媒体复制失败，请稍后重试");
            SetStatus($"媒体已复制：{Path.GetFileName(path)}");
        });
    }

    private async void VisualChat_PreviewRequested(ChatLine line)
    {
        if (DialogsList.SelectedItem is not DialogItem dialog || dialog.Id != line.ChatId) return;
        try
        {
            var path = await _telegram.DownloadMediaThumbnailAsync(dialog, line.MessageId);
            if (path is not null) VisualChat.SetPreview(line.MessageId, path);
        }
        catch (Exception ex)
        {
            _logger.Warning("Telegram.Media", $"缩略图加载失败：{dialog.Kind}/{dialog.Id}/{line.MessageId}", ex);
        }
    }

    private void BeginEditMessage(ChatLine line)
    {
        if (!line.IsOutgoing || line.MessageId <= 0) return;
        ClearQuoteSelection();
        _editingMessageId = line.MessageId;
        _editingMessage = line;
        QuotePreviewText.Text = $"✎ 编辑 #{line.MessageId}: {line.DisplayText}";
        QuotePreviewPanel.Visibility = Visibility.Visible;
        MessageBox.Text = line.Text;
        MessageBox.Focus();
        MessageBox.SelectAll();
    }

    private async void VisualChat_DeleteRequested(ChatLine line)
    {
        if (DialogsList.SelectedItem is not DialogItem dialog || dialog.Id != line.ChatId) return;
        var result = System.Windows.MessageBox.Show(
            $"确定撤回消息 #{line.MessageId}？", "TelegramConsole",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;
        await RunUiAsync(async () =>
        {
            await _telegram.DeleteMessagesAsync(dialog, [line.MessageId], true);
            var deletion = new MessageDeletion(
                DateTime.Now, dialog.Id, dialog.Kind, dialog.Name,
                [new DeletedMessageInfo(line.MessageId, line.Sender, line.DisplayText)]);
            ApplyDeletedMessage(deletion, deletion.Messages[0], notifyUnread: false);
            SetStatus($"消息 #{line.MessageId} 已撤回");
        });
    }

    private async void VisualChat_CopyLinkRequested(ChatLine line)
    {
        if (DialogsList.SelectedItem is not DialogItem dialog || dialog.Id != line.ChatId) return;
        var link = _telegram.GetMessageLink(dialog, line.MessageId);
        if (string.IsNullOrWhiteSpace(link))
            SetStatus("当前会话无法生成公开消息链接");
        else
        {
            SetStatus(await ClipboardHelper.TrySetTextAsync(link)
                ? $"消息链接已复制：{link}"
                : "复制失败，请稍后重试");
        }
    }

    private async void VisualChat_CopyTextRequested(ChatLine line)
    {
        if (string.IsNullOrWhiteSpace(line.DisplayText)) return;
        SetStatus(await ClipboardHelper.TrySetTextAsync(line.DisplayText)
            ? "消息正文已复制"
            : "复制失败，请稍后重试");
    }

    private async void VisualChat_ReactionRequested(ChatLine line, string emoji)
    {
        if (DialogsList.SelectedItem is not DialogItem dialog || dialog.Id != line.ChatId) return;
        await RunUiAsync(async () =>
        {
            await _telegram.SendReactionAsync(dialog, line.MessageId, emoji);
            ApplyChatLineToCurrentView(line with { Reactions = ApplyLocalReaction(line.Reactions, emoji) });
            SetStatus($"已回应 {emoji} 到消息 #{line.MessageId}");
        });
    }

    private static IReadOnlyList<ChatReaction> ApplyLocalReaction(
        IReadOnlyList<ChatReaction>? current,
        string emoji)
    {
        var reactions = (current ?? []).ToList();
        for (var index = reactions.Count - 1; index >= 0; index--)
        {
            var reaction = reactions[index];
            if (!reaction.IsChosen || reaction.Symbol == emoji) continue;
            if (reaction.Count <= 1) reactions.RemoveAt(index);
            else reactions[index] = reaction with { Count = reaction.Count - 1, IsChosen = false };
        }

        var targetIndex = reactions.FindIndex(x => x.Symbol == emoji);
        if (targetIndex < 0)
            reactions.Add(new ChatReaction(emoji, 1, true));
        else if (!reactions[targetIndex].IsChosen)
            reactions[targetIndex] = reactions[targetIndex] with
            {
                Count = reactions[targetIndex].Count + 1,
                IsChosen = true
            };
        return reactions;
    }

    private async void VisualChat_ForwardRequested(ChatLine line)
    {
        if (DialogsList.SelectedItem is not DialogItem source || source.Id != line.ChatId) return;
        var picker = new ForwardTargetWindow(_allDialogs) { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedDialog is not DialogItem target) return;
        await RunUiAsync(async () =>
        {
            await _telegram.ForwardMessagesAsync(source, [line.MessageId], target);
            SetStatus($"消息已转发到 {target.Name}");
        });
    }

    private async void MessageBox_KeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key != Key.Enter) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            e.Handled = true;
            InsertLineBreak(MessageBox);
            return;
        }
        e.Handled = true;
        if (string.IsNullOrWhiteSpace(MessageBox.Text)) return;
        await RunUiAsync(SendMessageAsync);
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

    private void MessageBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateChatSendButton();

    private async Task SendMessageAsync()
    {
        if (DialogsList.SelectedItem is not DialogItem dialog)
            throw new InvalidOperationException(L("SelectChatFirst"));
        var text = MessageBox.Text.Trim();
        if (text.Length == 0) throw new InvalidOperationException(L("MessageRequired"));
        SetChatSendEnabled(false);
        SetStatus(L("SendingMessage"));
        try
        {
            if (_editingMessageId is int editingMessageId)
            {
                var editingMessage = _editingMessage;
                await _telegram.EditMessageAsync(dialog, editingMessageId, text);
                if (editingMessage is not null && editingMessage.MessageId == editingMessageId)
                    ApplyChatLineToCurrentView(editingMessage with { Text = text, IsEdited = true });
                MessageBox.Clear();
                SetStatus($"消息 #{editingMessageId} 已编辑");
                ClearQuoteSelection();
                return;
            }
            var quoteTarget = _selectedQuoteTarget;
            RequestScrollAfterSend(dialog);
            if (quoteTarget is null)
                await _telegram.SendAsync(dialog, text);
            else
                await _telegram.SendReplyAsync(dialog, quoteTarget.MessageId, text, quoteTarget.Text);
            ScrollCurrentChatToEnd();
            MessageBox.Clear();
            SetStatus(quoteTarget is null ? LF("MessageSent", dialog.Name) : LF("QuoteReplySent", dialog.Name));
            ClearQuoteSelection();
        }
        catch
        {
            _pendingSentScroll = null;
            throw;
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

    private async void Terminal_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not BufferedTerminal terminal) return;
        var media = terminal.GetTagAtVisualPosition<MediaLinkItem>(
            e.GetPosition(terminal.TextArea.TextView), preferSelection: false);
        if (media is null) return;
        e.Handled = true;
        await RunUiAsync(async () =>
        {
            SetStatus(LF("DownloadingMedia", media.Label));
            var path = await _telegram.DownloadMediaAsync(media.Dialog, media.MessageId);
            if (MediaFileLauncher.Open(this, path)) SetStatus(LF("MediaOpened", Path.GetFileName(path)));
        });
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
        _editingMessageId = null;
        _editingMessage = null;
        _selectedQuoteTarget = target;
        QuotePreviewText.Text = $"↪ {target.DisplayText}";
        QuotePreviewPanel.Visibility = Visibility.Visible;
        MessageBox.Focus();
    }

    private void ClearQuoteSelection()
    {
        _editingMessageId = null;
        _editingMessage = null;
        _selectedQuoteTarget = null;
        _contextQuoteTarget = null;
        QuotePreviewText.Text = string.Empty;
        QuotePreviewPanel.Visibility = Visibility.Collapsed;
    }

    private void SetChatSendEnabled(bool enabled)
    {
        MessageBox.IsEnabled = enabled;
        SendButton.IsEnabled = enabled && !string.IsNullOrWhiteSpace(MessageBox.Text);
    }

    private void UpdateChatSendButton() =>
        SendButton.IsEnabled = MessageBox.IsEnabled && !string.IsNullOrWhiteSpace(MessageBox.Text);

    private void HandleIncoming(ChatLine line)
    {
        if (_settings.MonitorEnabled && line.IsGroup)
        {
            AppendChatLine(MonitorConsole, line);
            if (!line.IsEdited) MarkTabUnread(MonitorTab);
        }
        if (DialogsList.SelectedItem is DialogItem current && current.Id == line.ChatId)
        {
            if (ApplyChatLineToCurrentView(line)) MarkTabUnread(ChatTab);
        }
    }

    private bool ApplyChatLineToCurrentView(ChatLine line)
    {
        if (DialogsList.SelectedItem is not DialogItem current || current.Id != line.ChatId) return false;
        var isNew = line.MessageId <= 0 || !_chatTimeline.Any(x => x.MessageId == line.MessageId &&
            x.ChatId == line.ChatId && string.Equals(x.ChatKind, line.ChatKind, StringComparison.Ordinal));
        var forceScrollToEnd = ConsumePendingSentScroll(line);
        var requiresRender = MergeChatTimeline(line);
        VisualChat.UpsertMessage(line, forceScrollToEnd);
        if (requiresRender) RenderChatTimeline(current, forceScrollToEnd);
        else AppendChatLine(ChatConsole, line, forceScrollToEnd);
        return isNew;
    }

    private void RequestScrollAfterSend(DialogItem dialog) =>
        _pendingSentScroll = (dialog.Kind, dialog.Id, DateTime.UtcNow.AddSeconds(30));

    private bool ConsumePendingSentScroll(ChatLine line)
    {
        if (_pendingSentScroll is not { } pending || DateTime.UtcNow > pending.ExpiresUtc)
        {
            _pendingSentScroll = null;
            return false;
        }
        if (!line.IsOutgoing || line.ChatId != pending.Id ||
            !string.Equals(line.ChatKind, pending.Kind, StringComparison.Ordinal)) return false;
        _pendingSentScroll = null;
        return true;
    }

    private void ScrollCurrentChatToEnd()
    {
        ChatConsole.ScrollToEnd();
        VisualChat.ScrollToEnd();
    }

    private bool MergeChatTimeline(ChatLine line)
    {
        var existingIndex = line.MessageId > 0
            ? _chatTimeline.FindIndex(x => x.MessageId == line.MessageId && x.ChatId == line.ChatId &&
                                           string.Equals(x.ChatKind, line.ChatKind, StringComparison.Ordinal))
            : -1;
        if (existingIndex >= 0)
        {
            _chatTimeline[existingIndex] = line;
            return true;
        }

        var previous = _chatTimeline.LastOrDefault();
        var requiresRender = previous is not null &&
                             (line.Time < previous.Time ||
                              line.Time == previous.Time && line.MessageId > 0 && previous.MessageId > line.MessageId);
        _chatTimeline.Add(line);
        var ordered = _chatTimeline.OrderBy(x => x.Time).ThenBy(x => x.MessageId).TakeLast(QuoteHistoryLimit).ToArray();
        if (ordered.Length != _chatTimeline.Count) requiresRender = true;
        _chatTimeline.Clear();
        _chatTimeline.AddRange(ordered);
        return requiresRender;
    }

    private void RenderChatTimeline(DialogItem dialog, bool forceScrollToEnd = false)
    {
        var blocks = new List<TerminalBlock>
        {
            new([($"--- {dialog.Name} ---", Brushes.White, null)])
        };
        var timeline = _chatTimeline
            .Select(line => (line.Time, Order: 0, line.MessageId, Block: CreateChatTerminalBlock(line)))
            .Concat(_chatDeletions.Values
                .Where(x => x.Deletion.ChatId == dialog.Id &&
                            string.Equals(x.Deletion.ChatKind, dialog.Kind, StringComparison.Ordinal))
                .Select(x => (x.Deletion.Time, Order: 1, x.Message.MessageId,
                    Block: CreateDeletionTerminalBlock(x.Deletion, x.Message))))
            .OrderBy(x => x.Time)
            .ThenBy(x => x.MessageId)
            .ThenBy(x => x.Order);
        blocks.AddRange(timeline.Select(x => x.Block));
        ChatConsole.ReplaceBlocks(blocks, forceScrollToEnd);
    }

    private void Telegram_MessageDeleted(MessageDeletion deletion) => Dispatcher.BeginInvoke(() =>
    {
        foreach (var message in deletion.Messages)
            ApplyDeletedMessage(deletion, message, notifyUnread: true);
    });

    private void ApplyDeletedMessage(
        MessageDeletion deletion,
        DeletedMessageInfo message,
        bool notifyUnread)
    {
        var cacheKey = (deletion.ChatKind, deletion.ChatId, message.MessageId);
        _chatDeletions[cacheKey] = new CachedDeletion(deletion, message);
        while (_chatDeletions.Count > QuoteHistoryLimit)
            _chatDeletions.Remove(_chatDeletions.First().Key);
        var deletionBlock = CreateDeletionTerminalBlock(deletion, message);
        var deduplicationKey = ("deleted", deletion.ChatKind, deletion.ChatId, message.MessageId);
        if (_settings.MonitorEnabled && deletion.ChatKind is "Chat" or "Channel")
        {
            MonitorConsole.AppendLines(deletionBlock.Lines, deduplicationKey);
            if (notifyUnread) MarkTabUnread(MonitorTab);
        }
        if (DialogsList.SelectedItem is DialogItem current && current.Id == deletion.ChatId &&
            string.Equals(current.Kind, deletion.ChatKind, StringComparison.Ordinal))
        {
            ChatConsole.AppendLines(deletionBlock.Lines, deduplicationKey);
            VisualChat.MarkDeleted(message);
            if (notifyUnread) MarkTabUnread(ChatTab);
        }
    }

    private static TerminalBlock CreateDeletionTerminalBlock(MessageDeletion deletion, DeletedMessageInfo message)
    {
        var sender = string.IsNullOrWhiteSpace(message.Sender) ? "未知发送人" : message.Sender;
        var marker = $"[{deletion.Time:HH:mm:ss}] [{deletion.Chat}] ↩ 已撤回 #{message.MessageId} {sender}: {message.Text}";
        return new TerminalBlock(
            [(marker, Brushes.Orange, null)],
            ("deleted", deletion.ChatKind, deletion.ChatId, message.MessageId));
    }

    private sealed record CachedDeletion(MessageDeletion Deletion, DeletedMessageInfo Message);

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, MainTabs) || MainTabs.SelectedItem is not TabItem selected) return;
        selected.Tag = Visibility.Collapsed;
    }

    private void MarkTabUnread(TabItem tab)
    {
        if (_unreadNotificationsReady && !ReferenceEquals(MainTabs.SelectedItem, tab))
            tab.Tag = Visibility.Visible;
    }

    private void ClearAllUnreadIndicators()
    {
        foreach (var tab in MainTabs.Items.OfType<TabItem>()) tab.Tag = Visibility.Collapsed;
    }

    private void ResetUnreadBaselines()
    {
        _outboxBaselineReady = _mentionBaselineReady = _exceptionBaselineReady = false;
        _knownOutboxIds.Clear();
        _knownMentionIds.Clear();
        _knownExceptionIds.Clear();
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
            _store.SaveAccount(_activeAccount);
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
        _store.SaveAccount(_activeAccount);
        foreach (var row in selected) await _scheduler.DeleteAsync(row.Id);
        RenderSchedules();
        SetStatus(LF("TasksDeleted", selected.Count));
    }

    private async void AddIntervalRule_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync(async () =>
        {
            if (_activeAccount is null) throw new InvalidOperationException(L("LoginFirst"));
            if (IntervalSourceBox.SelectedItem is not DialogItem source)
                throw new InvalidOperationException(L("SelectSourceChat"));
            if (IntervalTargetBox.SelectedItem is not DialogItem target)
                throw new InvalidOperationException(L("SelectTargetChat"));
            if (!int.TryParse(IntervalMinutesBox.Text.Trim(), out var interval) || interval is < 1 or > 1440)
                throw new InvalidOperationException(L("InvalidIntervalMinutes"));
            if (!int.TryParse(IntervalMinimumBox.Text.Trim(), out var minimum) || minimum is < 1 or > 300)
                throw new InvalidOperationException(L("InvalidMinimumMessages"));
            if (!int.TryParse(IntervalSummaryLinesBox.Text.Trim(), out var summaryLines) || summaryLines is < 1 or > 30)
                throw new InvalidOperationException(L("InvalidSummaryLines"));

            var now = DateTimeOffset.Now;
            var rule = new IntervalChatRule
            {
                Name = string.IsNullOrWhiteSpace(IntervalNameBox.Text) ? L("DefaultDigestName") : IntervalNameBox.Text.Trim(),
                SourceChatId = source.Id,
                SourceChatKind = source.Kind,
                SourceChatTitle = source.Name,
                TargetChatId = target.Id,
                TargetChatKind = target.Kind,
                TargetChatTitle = target.Name,
                IntervalMinutes = interval,
                MinimumMessageCount = minimum,
                SummaryLineCount = summaryLines,
                WindowStartedAt = now,
                LastCheckedAt = now
            };
            _activeAccount.IntervalChatRules.Add(rule);
            _store.SaveAccount(_activeAccount);
            await _intervalChatAutomation.UpsertAsync(rule);
            RenderIntervalChatRules();
        });

    private async void RunIntervalRuleNow_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync(async () =>
        {
            var selected = GetCheckedIntervalRuleRows();
            if (selected.Count == 0) throw new InvalidOperationException(L("SelectIntervalRule"));
            foreach (var row in selected) await _intervalChatAutomation.ExecuteNowAsync(row.Id);
            RenderIntervalChatRules();
        });

    private async void RemoveIntervalRule_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync(async () =>
        {
            if (_activeAccount is null) throw new InvalidOperationException(L("LoginFirst"));
            var selected = GetCheckedIntervalRuleRows();
            if (selected.Count == 0) throw new InvalidOperationException(L("SelectIntervalRule"));
            foreach (var row in selected) await _intervalChatAutomation.DeleteAsync(row.Id);
            RenderIntervalChatRules();
        });

    private async void IntervalRuleList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IntervalRuleList.SelectedItem is not IntervalRuleRow row || _activeAccount is null) return;
        var rule = _activeAccount.IntervalChatRules.FirstOrDefault(x => x.Id == row.Id);
        if (rule is null) return;
        rule.Enabled = !rule.Enabled;
        _store.SaveAccount(_activeAccount);
        await _intervalChatAutomation.UpsertAsync(rule);
        RenderIntervalChatRules();
    }

    private void RenderIntervalChatRules()
    {
        if (IntervalRuleList is null) return;
        IntervalRuleList.ItemsSource = (_activeAccount?.IntervalChatRules ?? [])
            .OrderBy(x => x.Name)
            .Select(x => new IntervalRuleRow(
                x.Id,
                x.Enabled ? L("Enabled") : L("Disabled"),
                x.Name,
                x.SourceChatTitle,
                x.TargetChatTitle,
                LF("EveryMinutes", x.IntervalMinutes),
                $"{x.LastObservedMessageCount}/{x.MinimumMessageCount}",
                x.LastSentAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "-",
                x.LastStatus))
            .ToList();
    }

    private List<IntervalRuleRow> GetCheckedIntervalRuleRows() =>
        IntervalRuleList.Items.Cast<IntervalRuleRow>().Where(x => x.IsChecked).ToList();

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
            _store.SaveAccount(_activeAccount!);
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
        if (_activeAccount is not null) _store.SaveAccount(_activeAccount);
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

        DisposeWorkspaceResources();
    }

    internal void StopWorkspace()
    {
        _exitRequested = true;
        Close();
    }

    internal void ShowWorkspace()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void DisposeWorkspaceResources()
    {
        if (_resourcesDisposed) return;
        _resourcesDisposed = true;
        _logger.Info("Application", "应用正在退出");
        if (int.TryParse(ApiIdBox.Text, out var apiId)) _settings.ApiId = apiId;
        _settings.ApiHash = ApiHashBox.Password.Trim();
        _settings.PhoneNumber = PhoneBox.Text.Trim();
        _store.Save(_settings);
        if (_activeAccount is not null) _store.SaveAccount(_activeAccount);
        _exceptionMonitor.RecordsChanged -= ExceptionMonitor_RecordsChanged;
        _exceptionMonitor.Dispose();
        _mentionMonitor.RecordsChanged -= MentionMonitor_RecordsChanged;
        _mentionMonitor.Dispose();
        _telegram.MessageDeleted -= Telegram_MessageDeleted;
        _telegram.OutboxChanged -= Telegram_OutboxChanged;
        _telegram.AutomationActivity -= Telegram_AutomationActivity;
        _scheduler.Dispose();
        _intervalChatAutomation.Dispose();
        _telegram.Dispose();
        if (!_managedWorkspace)
        {
            Application.Current.DispatcherUnhandledException -= Application_DispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;
            AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
        }
        _logger.Dispose();
        DisposeTrayIcon();
    }

    private void DisposeTrayIcon()
    {
        AccountWorkspaceManager.Changed -= UpdateTrayWorkspaces;
        if (_trayIcon is null) return;
        _trayIcon.Visible = false;
        _trayIcon.ContextMenuStrip?.Dispose();
        _trayIcon.Icon?.Dispose();
        _trayIcon.Dispose();
        _trayIcon = null;
    }

    private void Logger_EntryWritten(AppLogEntry entry)
    {
        var notifyUnread = _unreadNotificationsReady;
        Dispatcher.BeginInvoke(() =>
        {
            AppendLogEntry(entry);
            if (notifyUnread) MarkTabUnread(RuntimeLogsTab);
        });
    }

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

    private void Telegram_OutboxChanged()
    {
        var notifyUnread = _unreadNotificationsReady;
        Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                if (await RefreshOutboxAsync() && notifyUnread) MarkTabUnread(OutboxTab);
            }
            catch (Exception ex) { _logger.Error("Outbox", "刷新发件箱失败", ex); }
        });
    }

    private void Telegram_AutomationActivity(string message)
    {
        var notifyUnread = _unreadNotificationsReady;
        Dispatcher.BeginInvoke(() =>
        {
            SetStatus(message);
            AppendConsole(MonitorConsole, $"[{DateTime.Now:HH:mm:ss}] [规则] {message}");
            if (notifyUnread) MarkTabUnread(MonitorTab);
        });
    }

    private async void RefreshOutbox_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync(async () => { await RefreshOutboxAsync(); });

    private async Task<bool> RefreshOutboxAsync()
    {
        var records = await _telegram.QueryOutboxAsync();
        var currentIds = records.Select(x => x.Id).ToHashSet();
        var hasAddedRows = _outboxBaselineReady && currentIds.Except(_knownOutboxIds).Any();
        _knownOutboxIds = currentIds;
        _outboxBaselineReady = true;
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
        return hasAddedRows;
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
        _store.SaveAccount(_activeAccount);
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
        await RunUiAsync(async () => { await RefreshMentionsAsync(); });

    private async void ResetMentionQuery_Click(object sender, RoutedEventArgs e)
    {
        MentionKeywordBox.Clear();
        await RunUiAsync(async () => { await RefreshMentionsAsync(); });
    }

    private async Task<bool> RefreshMentionsAsync()
    {
        var records = await _mentionMonitor.QueryAsync(new MentionQuery(MentionKeywordBox.Text.Trim()));
        var currentIds = records.Select(x => x.Id).ToHashSet();
        var hasAddedRows = _mentionBaselineReady && currentIds.Except(_knownMentionIds).Any();
        _knownMentionIds = currentIds;
        _mentionBaselineReady = true;
        MentionList.ItemsSource = records.Select(x => new MentionRow(
            x.Id,
            x.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            x.ChatName,
            x.Sender,
            x.Message,
            x.NotificationStatus)).ToList();
        return hasAddedRows;
    }

    private void MentionMonitor_RecordsChanged()
    {
        var notifyUnread = _unreadNotificationsReady;
        Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                if (await RefreshMentionsAsync() && notifyUnread) MarkTabUnread(MentionsTab);
            }
            catch (Exception ex) { _logger.Error("MentionMonitor", "刷新@消息列表失败", ex); }
        });
    }

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
        alerts.EmailNotificationsEnabled = ExceptionEmailNotifyEnabledBox.IsChecked == true;
        alerts.ManualLoginEmailReminderEnabled = ManualLoginEmailReminderBox.IsChecked == true;
        alerts.MinimumLevel = ExceptionMinimumLevelBox.SelectedIndex == 1
            ? AppLogLevel.Critical
            : AppLogLevel.Error;
        alerts.TelegramPeerId = target?.Dialog?.Id;
        alerts.TelegramPeerKind = target?.Kind ?? "";
        alerts.TelegramPeerTitle = target?.Dialog?.Name ?? "";
        alerts.EmailRecipient = ExceptionEmailBox.Text.Trim();
        EnsureEmailConfigured(alerts.EmailRecipient);
        _store.SaveAccount(_activeAccount);
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
        await RunUiAsync(async () => { await RefreshExceptionsAsync(); });
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

    private async Task<bool> RefreshExceptionsAsync()
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
        var currentIds = records.Select(x => x.Id).ToHashSet();
        var hasAddedRows = _exceptionBaselineReady && currentIds.Except(_knownExceptionIds).Any();
        _knownExceptionIds = currentIds;
        _exceptionBaselineReady = true;
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
        return hasAddedRows;
    }

    private async void ResetExceptionQuery_Click(object sender, RoutedEventArgs e)
    {
        ExceptionFromDatePicker.SelectedDate = DateTime.Today;
        ExceptionToDatePicker.SelectedDate = DateTime.Today;
        ExceptionQueryLevelBox.SelectedIndex = 0;
        ExceptionKeywordBox.Clear();
        _exceptionQueryLimit = 10;
        await RunUiAsync(async () => { await RefreshExceptionsAsync(); });
    }

    private static DateTimeOffset? ToLocalDateOffset(DateTime? date)
    {
        if (date is null) return null;
        var localDate = DateTime.SpecifyKind(date.Value.Date, DateTimeKind.Unspecified);
        return new DateTimeOffset(localDate, TimeZoneInfo.Local.GetUtcOffset(localDate));
    }

    private void ExceptionMonitor_RecordsChanged()
    {
        var notifyUnread = _unreadNotificationsReady;
        Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                if (await RefreshExceptionsAsync() && notifyUnread) MarkTabUnread(ExceptionsTab);
            }
            catch (Exception ex) { _logger.Error("ExceptionMonitor", "刷新异常列表失败", ex); }
        });
    }

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

    private static void AppendChatLine(BufferedTerminal box, ChatLine line, bool forceScrollToEnd = false)
    {
        var block = CreateChatTerminalBlock(line);
        box.AppendLines(block.Lines, block.DeduplicationKey, block.InlineLinks, forceScrollToEnd);
    }

    private static TerminalBlock CreateChatTerminalBlock(ChatLine line)
    {
        var body = $"[{line.Time:HH:mm:ss}] [{line.Chat}] {line.Sender}: {line.DisplayText}";
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
        return new TerminalBlock(
        [
            ($"↪ {sender}: {PreviewReply(line.ReplyText)}", Brushes.Gray,
                new QuoteTargetItem(replyId, sender, line.ReplyText)),
            (body, color, messageTag)
        ], deduplicationKey, MediaLinkFactory.Create(line, body, lineIndex: 1));
    }

    private static string FormatReactions(IReadOnlyList<ChatReaction>? reactions)
    {
        if (reactions is null || reactions.Count == 0) return "";
        return "  " + string.Join(" ", reactions.Select(x => x.Count > 1 ? $"{x.Symbol}×{x.Count}" : x.Symbol));
    }

    private static string PreviewReply(string text)
    {
        var value = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 120 ? value : value[..117] + "...";
    }

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

    private sealed class IntervalRuleRow(
        Guid id,
        string enabledText,
        string name,
        string sourceTitle,
        string targetTitle,
        string intervalText,
        string messageCountText,
        string lastSentText,
        string lastStatus)
    {
        public bool IsChecked { get; set; }
        public Guid Id { get; } = id;
        public string EnabledText { get; } = enabledText;
        public string Name { get; } = name;
        public string SourceTitle { get; } = sourceTitle;
        public string TargetTitle { get; } = targetTitle;
        public string IntervalText { get; } = intervalText;
        public string MessageCountText { get; } = messageCountText;
        public string LastSentText { get; } = lastSentText;
        public string LastStatus { get; } = lastStatus;
    }

    private static string L(string key) => LocalizationManager.Text(key);
    private static string LF(string key, params object?[] args) => LocalizationManager.Format(key, args);

    private sealed record ConfirmationTarget(DialogItem? Dialog, string Kind, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record DialogTypeFilter(DialogCategory? Category, string Name);

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

internal enum ChatPresentationMode
{
    Console,
    Visual,
    Split
}
