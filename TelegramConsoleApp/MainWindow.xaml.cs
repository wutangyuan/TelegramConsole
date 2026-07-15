using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Diagnostics;
using System.IO;

namespace TelegramConsoleApp;

public partial class MainWindow : Window
{
    private readonly ISettingsStore _store = new SettingsStore();
    private readonly IAppLogger _logger = new Log4NetAppLogger();
    private readonly AppSettings _settings;
    private readonly ITelegramService _telegram;
    private readonly ISchedulerService _scheduler;
    private readonly IExceptionMonitorService _exceptionMonitor;
    private AccountProfile? _activeAccount;
    private List<DialogItem> _allDialogs = [];
    private bool _loadingHistory;
    private bool _initialized;
    private GridLength _expandedDialogWidth = new(290);

    public MainWindow()
    {
        InitializeComponent();
        _settings = _store.Load();
        _telegram = new TelegramService(_store, _logger);
        _exceptionMonitor = new ExceptionMonitorService(_store, _logger, _telegram, _settings);
        _scheduler = new SchedulerService(_telegram, _store, _settings, _logger);

        ApiIdBox.Text = _settings.ApiId == 0 ? "" : _settings.ApiId.ToString();
        ApiHashBox.Password = _settings.ApiHash;
        PhoneBox.Text = _settings.PhoneNumber;
        MonitorEnabledBox.IsChecked = _settings.MonitorEnabled;
        SmtpHostBox.Text = _settings.Email.SmtpHost;
        SmtpPortBox.Text = _settings.Email.SmtpPort.ToString();
        SmtpUserBox.Text = _settings.Email.UserName;
        SmtpPasswordBox.Password = _settings.Email.Password;
        SmtpFromBox.Text = _settings.Email.FromAddress;
        SmtpSslBox.IsChecked = _settings.Email.EnableSsl;
        ExceptionNotifyEnabledBox.IsChecked = true;
        ExceptionMinimumLevelBox.SelectedIndex = 0;
        ExceptionQueryLevelBox.SelectedIndex = 0;
        ExceptionEmailBox.Text = "";
        SchedulePeriodBox.SelectedIndex = 0;
        AddMonday.IsChecked = true;
        RenderSchedules();
        SetStatus("请配置 Telegram API 参数后点击“一键登录”");

        _telegram.MessageReceived += line => Dispatcher.BeginInvoke(() => HandleIncoming(line));
        _telegram.Log += text => Dispatcher.BeginInvoke(() => SetStatus(text));
        _scheduler.Status += text => Dispatcher.BeginInvoke(() =>
        {
            SetStatus(text);
            AppendConsole(MonitorConsole, $"[{DateTime.Now:HH:mm:ss}] [任务] {text}");
        });
        _logger.EntryWritten += Logger_EntryWritten;
        _exceptionMonitor.RecordsChanged += ExceptionMonitor_RecordsChanged;
        Application.Current.DispatcherUnhandledException += Application_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        _logger.Info("Application", "WPF 主窗口已初始化");
        Closing += MainWindow_Closing;
        Loaded += async (_, _) => await RefreshExceptionsAsync();
        _initialized = true;
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync(BeginLoginAsync);

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_settings, _store) { Owner = this };
        if (window.ShowDialog() == true)
            SetStatus(_settings.Proxy.Enabled
                ? $"代理设置已加密保存：{(_settings.Proxy.Type == "MtProxy" ? "MTProxy" : $"SOCKS5 {_settings.Proxy.Host}:{_settings.Proxy.Port}")}"
                : "代理已关闭，将使用直连");
    }

    private async Task BeginLoginAsync()
    {
        if (!int.TryParse(ApiIdBox.Text.Trim(), out var apiId) || apiId <= 0)
            throw new InvalidOperationException("API ID 必须是正整数");
        if (string.IsNullOrWhiteSpace(ApiHashBox.Password) || string.IsNullOrWhiteSpace(PhoneBox.Text))
            throw new InvalidOperationException("请填写 API Hash 和手机号（含国家区号）");

        _settings.ApiId = apiId;
        _settings.ApiHash = ApiHashBox.Password.Trim();
        _settings.PhoneNumber = PhoneBox.Text.Trim();
        _store.Save(_settings);
        await DeactivateAccountAsync();
        SetLoginBusy(true);
        SetStatus("正在连接 Telegram...");
        await HandleLoginResultAsync(await _telegram.BeginLoginAsync(_settings));
    }

    private async Task DeactivateAccountAsync()
    {
        _activeAccount = null;
        _exceptionMonitor.DeactivateAccount();
        await _scheduler.DeactivateAccountAsync();
        _allDialogs = [];
        DialogsList.ItemsSource = null;
        ScheduleChatBox.ItemsSource = null;
        ConfirmationPeerBox.ItemsSource = null;
        ExceptionPeerBox.ItemsSource = null;
        ExceptionNotifyEnabledBox.IsChecked = false;
        ExceptionMinimumLevelBox.SelectedIndex = 0;
        ExceptionEmailBox.Clear();
        ScheduleList.ItemsSource = null;
        ExceptionList.ItemsSource = null;
        ChatConsole.Document.Blocks.Clear();
        MonitorConsole.Document.Blocks.Clear();
    }

    private async void ContinueLoginButton_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync(ContinueLoginAsync);

    private async Task ContinueLoginAsync()
    {
        var value = LoginPasswordBox.Visibility == Visibility.Visible
            ? LoginPasswordBox.Password
            : LoginValueBox.Text;
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("请输入登录所需信息");
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
            SetStatus($"Telegram 要求输入：{PromptText(prompt)}");
            return;
        }

        LoginPromptLabel.Visibility = LoginValueBox.Visibility = LoginPasswordBox.Visibility =
            ContinueLoginButton.Visibility = Visibility.Collapsed;
        SetLoginBusy(false);
        SetStatus($"已登录：{_telegram.CurrentUser}");
        await ActivateAccountAsync();
        await LoadDialogsAsync();
        await _scheduler.RunDueTasksAsync();
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
        ChatConsole.Document.Blocks.Clear();
        MonitorConsole.Document.Blocks.Clear();
        ConfirmationPeerBox.ItemsSource = null;
        ExceptionPeerBox.ItemsSource = null;
        ExceptionNotifyEnabledBox.IsChecked = account.ExceptionAlerts.NotificationsEnabled;
        ExceptionMinimumLevelBox.SelectedIndex = account.ExceptionAlerts.MinimumLevel == AppLogLevel.Critical ? 1 : 0;
        ExceptionEmailBox.Text = account.ExceptionAlerts.EmailRecipient;
        _exceptionMonitor.ActivateAccount(userId, account.ExceptionAlerts);
        await _scheduler.ActivateAccountAsync(account);
        RenderSchedules();
        await RefreshExceptionsAsync();
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
        SetStatus($"已加载 {_allDialogs.Count} 个会话，其中 {groups.Count} 个群聊/频道");
    }

    private void OpenConsole_Click(object sender, RoutedEventArgs e) => OpenSelectedConsole();

    private void DialogsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedConsole();

    private void OpenSelectedConsole()
    {
        if (DialogsList.SelectedItem is not DialogItem dialog)
        {
            ShowError("请先选择要打开的私聊或群聊");
            return;
        }
        var window = new ChatConsoleWindow(_telegram, dialog);
        window.Show();
    }

    private void BlankConsole_Click(object sender, RoutedEventArgs e)
    {
        DialogsList.SelectedItem = null;
        ChatConsole.Document.Blocks.Clear();
        SetStatus("聊天终端已切换为空屏；重新选择会话后恢复显示");
    }

    private void ToggleDialogsButton_Click(object sender, RoutedEventArgs e)
    {
        if (DialogColumn.Width.Value > 0)
        {
            _expandedDialogWidth = DialogColumn.Width;
            DialogColumn.Width = new GridLength(0);
            ToggleDialogsButton.Content = "❯";
            ToggleDialogsButton.ToolTip = "展开会话列表";
        }
        else
        {
            DialogColumn.Width = _expandedDialogWidth.Value > 0 ? _expandedDialogWidth : new GridLength(290);
            ToggleDialogsButton.Content = "❮";
            ToggleDialogsButton.ToolTip = "收起会话列表";
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
                ChatConsole.Document.Blocks.Clear();
                AppendConsole(ChatConsole, $"--- {dialog.Name} ---");
                foreach (var line in await _telegram.LoadHistoryAsync(dialog)) AppendChatLine(ChatConsole, line);
            }
            finally
            {
                _loadingHistory = false;
            }
        });
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync(SendMessageAsync);

    private async void MessageBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        await RunUiAsync(SendMessageAsync);
    }

    private async Task SendMessageAsync()
    {
        if (DialogsList.SelectedItem is not DialogItem dialog)
            throw new InvalidOperationException("请先在左侧选择私聊或群聊");
        var text = MessageBox.Text.Trim();
        if (text.Length == 0) return;
        await _telegram.SendAsync(dialog, text);
        MessageBox.Clear();
        AppendConsole(ChatConsole, $"[{DateTime.Now:HH:mm:ss}] 我: {text}", Brushes.LimeGreen);
        SetStatus($"消息已发送到 {dialog.Name}");
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
            if (_activeAccount is null) throw new InvalidOperationException("请先登录 Telegram 账号");
            if (ScheduleChatBox.SelectedItem is not DialogItem group)
                throw new InvalidOperationException("请先登录并选择目标群聊");
            if (!TimeSpan.TryParseExact(ScheduleTimeBox.Text.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var time))
                throw new InvalidOperationException("时间格式应为 HH:mm，例如 08:00");
            if (time >= TimeSpan.FromDays(1)) throw new InvalidOperationException("请输入 00:00 到 23:59 之间的时间");
            var message = ScheduleMessageBox.Text.Trim();
            if (message.Length == 0) throw new InvalidOperationException("发送内容不能为空");
            var confirmationTarget = ConfirmationPeerBox.SelectedItem as ConfirmationTarget;
            var confirmationText = ConfirmationTextBox.Text.Trim();
            if (confirmationText.Length == 0) confirmationText = "签到完成：{群聊}，时间 {时间}";
            var confirmationEmail = ConfirmationEmailBox.Text.Trim();
            if (confirmationEmail.Length > 0) SaveEmailSettings(false);
            var period = SchedulePeriodBox.SelectedIndex == 1 ? SchedulePeriod.Weekly : SchedulePeriod.Daily;
            var weekDays = GetSelectedWeekDays();
            if (period == SchedulePeriod.Weekly && weekDays.Count == 0)
                throw new InvalidOperationException("每周任务至少需要选择一天");

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
            SetStatus("定时任务已添加");
        }
        catch (Exception ex)
        {
            _logger.Error("UI", "界面操作失败", ex);
            ShowError(ex.Message);
        }
    }

    private async void RemoveSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (_activeAccount is null) return;
        var selected = ScheduleList.SelectedItems.Cast<ScheduleRow>().ToList();
        if (selected.Count == 0) return;
        var selectedIds = selected.Select(x => x.Id).ToHashSet();
        _activeAccount.Schedules.RemoveAll(x => selectedIds.Contains(x.Id));
        _store.Save(_settings);
        foreach (var row in selected) await _scheduler.DeleteAsync(row.Id);
        RenderSchedules();
        SetStatus($"已删除 {selected.Count} 个定时任务");
    }

    private async void EditSchedule_Click(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(async () =>
        {
            var selected = ScheduleList.SelectedItems.Cast<ScheduleRow>().ToList();
            if (selected.Count != 1) throw new InvalidOperationException("编辑时请只选择一个定时任务");
            var task = _activeAccount?.Schedules.FirstOrDefault(x => x.Id == selected[0].Id)
                ?? throw new InvalidOperationException("找不到选中的定时任务");
            var editor = new ScheduleEditWindow(task, _allDialogs) { Owner = this };
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
            var selected = ScheduleList.SelectedItems.Cast<ScheduleRow>().ToList();
            if (selected.Count == 0) throw new InvalidOperationException("请先选择至少一个定时任务");
            RunNowButton.IsEnabled = false;
            try
            {
                SetStatus($"正在立即执行 {selected.Count} 个任务...");
                var errors = new List<string>();
                foreach (var row in selected)
                {
                    try
                    {
                        await _scheduler.ExecuteNowAsync(row.Id);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{row.ChatTitle}: {ex.Message}");
                    }
                }
                RenderSchedules();
                if (errors.Count > 0)
                    throw new InvalidOperationException($"{selected.Count - errors.Count} 个成功，{errors.Count} 个失败：\n" + string.Join("\n", errors));
                SetStatus($"已完成 {selected.Count} 个任务的立即执行");
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
                x.Enabled ? "启用" : "停用",
                x.ChatTitle,
                DescribeSchedule(x),
                x.Message,
                BuildConfirmationSummary(x),
                x.LastSentDate?.ToString("yyyy-MM-dd") ?? "-"))
            .ToList();
    }

    private void SaveEmailSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveEmailSettings(true);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void SaveEmailSettings(bool showStatus)
    {
        if (!int.TryParse(SmtpPortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
            throw new InvalidOperationException("SMTP 端口不正确");
        _settings.Email.SmtpHost = SmtpHostBox.Text.Trim();
        _settings.Email.SmtpPort = port;
        _settings.Email.UserName = SmtpUserBox.Text.Trim();
        _settings.Email.Password = SmtpPasswordBox.Password;
        _settings.Email.FromAddress = SmtpFromBox.Text.Trim();
        _settings.Email.EnableSsl = SmtpSslBox.IsChecked == true;
        _store.Save(_settings);
        if (showStatus) SetStatus("邮件配置已加密保存");
    }

    private static string BuildConfirmationSummary(ScheduledMessage task)
    {
        var targets = new List<string>();
        if (task.ConfirmationPeerId is not null) targets.Add("TG: " + task.ConfirmationPeerTitle);
        if (!string.IsNullOrWhiteSpace(task.ConfirmationEmail)) targets.Add("邮件: " + task.ConfirmationEmail);
        return targets.Count == 0 ? "不通知" : string.Join(" / ", targets);
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
        if (task.Period == SchedulePeriod.Daily) return $"每天 {time}";
        var names = new Dictionary<DayOfWeek, string>
        {
            [DayOfWeek.Monday] = "一", [DayOfWeek.Tuesday] = "二", [DayOfWeek.Wednesday] = "三",
            [DayOfWeek.Thursday] = "四", [DayOfWeek.Friday] = "五", [DayOfWeek.Saturday] = "六",
            [DayOfWeek.Sunday] = "日"
        };
        return $"每周{string.Join('、', task.WeekDays.Select(x => names[x]))} {time}";
    }

    private async Task RunUiAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.Error("UI", "界面操作失败", ex);
            SetLoginBusy(false);
            ContinueLoginButton.IsEnabled = true;
            SetStatus("错误：" + ex.Message);
            ShowError(ex.Message);
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _logger.Info("Application", "应用正在退出");
        if (int.TryParse(ApiIdBox.Text, out var apiId)) _settings.ApiId = apiId;
        _settings.ApiHash = ApiHashBox.Password.Trim();
        _settings.PhoneNumber = PhoneBox.Text.Trim();
        if (int.TryParse(SmtpPortBox.Text.Trim(), out var smtpPort) && smtpPort is >= 1 and <= 65535)
        {
            _settings.Email.SmtpHost = SmtpHostBox.Text.Trim();
            _settings.Email.SmtpPort = smtpPort;
            _settings.Email.UserName = SmtpUserBox.Text.Trim();
            _settings.Email.Password = SmtpPasswordBox.Password;
            _settings.Email.FromAddress = SmtpFromBox.Text.Trim();
            _settings.Email.EnableSsl = SmtpSslBox.IsChecked == true;
        }
        _store.Save(_settings);
        _exceptionMonitor.RecordsChanged -= ExceptionMonitor_RecordsChanged;
        _exceptionMonitor.Dispose();
        _scheduler.Dispose();
        _telegram.Dispose();
        Application.Current.DispatcherUnhandledException -= Application_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
        _logger.EntryWritten -= Logger_EntryWritten;
        _logger.Dispose();
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
            ShowError(ex.Message);
        }
    }

    private void ClearLogDisplay_Click(object sender, RoutedEventArgs e) => LogConsole.Document.Blocks.Clear();

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

    private async void RefreshExceptions_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync(RefreshExceptionsAsync);

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
            from, toExclusive, level, ExceptionKeywordBox.Text.Trim()));
        ExceptionList.ItemsSource = records.Select(x => new ExceptionRow(
            x.Id,
            x.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            x.Level.ToString(),
            x.Category,
            x.Message,
            x.Details,
            x.TelegramStatus,
            x.EmailStatus)).ToList();
        SetStatus($"异常查询完成，共 {records.Count} 条");
    }

    private async void ResetExceptionQuery_Click(object sender, RoutedEventArgs e)
    {
        ExceptionFromDatePicker.SelectedDate = null;
        ExceptionToDatePicker.SelectedDate = null;
        ExceptionQueryLevelBox.SelectedIndex = 0;
        ExceptionKeywordBox.Clear();
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

    private void SetStatus(string text) => StatusText.Text = $"[{DateTime.Now:HH:mm:ss}] {text}";

    private void ShowError(string text) => System.Windows.MessageBox.Show(
        this, text, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);

    private static string PromptText(string prompt) => prompt switch
    {
        "verification_code" => "验证码",
        "password" => "两步验证密码",
        "name" => "姓名",
        "email" => "邮箱",
        "email_verification_code" => "邮箱验证码",
        _ => prompt
    };

    private static void AppendChatLine(RichTextBox box, ChatLine line) =>
        AppendConsole(
            box,
            $"[{line.Time:HH:mm:ss}] [{line.Chat}] {line.Sender}: {line.Text}",
            line.IsMentioned ? Brushes.DodgerBlue : line.IsOutgoing ? Brushes.LimeGreen : Brushes.White);

    private static void AppendConsole(RichTextBox box, string text, Brush? color = null)
    {
        var paragraph = new Paragraph(new Run(text))
        {
            Margin = new Thickness(0),
            Foreground = color ?? Brushes.White
        };
        box.Document.Blocks.Add(paragraph);
        box.ScrollToEnd();
    }

    private sealed record ScheduleRow(
        Guid Id,
        string EnabledText,
        string ChatTitle,
        string PeriodText,
        string Message,
        string Confirmation,
        string LastSentText);

    private sealed record ConfirmationTarget(DialogItem? Dialog, string Kind, string Name);

    private sealed record ExceptionRow(
        long Id,
        string OccurredAtText,
        string Level,
        string Category,
        string Message,
        string Details,
        string TelegramStatus,
        string EmailStatus);
}
