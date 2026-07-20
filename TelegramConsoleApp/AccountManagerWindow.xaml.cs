using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TelegramConsoleApp;

public partial class AccountManagerWindow : Window
{
    private readonly ISettingsStore _store;
    private readonly AppSettings _settings;
    private readonly IApplicationResourceMonitor _resourceMonitor;
    private readonly IExceptionLogQueryService _exceptionQuery;
    private readonly IAppLogger _logger;
    private readonly DispatcherTimer _refreshTimer;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private string _currentLogPath = "";
    private long _logPosition;
    private string _logRemainder = "";
    private bool _initialLogLoaded;
    private bool _exitRequested;
    private bool _disposed;
    private DateTime _nextAccountRefreshAt = DateTime.MinValue;
    private DateTime _nextExceptionRefreshAt = DateTime.MinValue;

    public AccountManagerWindow(ISettingsStore store)
    {
        _store = store;
        _settings = store.Load();
        _resourceMonitor = new ApplicationResourceMonitor(store.DataDirectory);
        _exceptionQuery = new ExceptionLogQueryService(store);
        _logger = new Log4NetAppLogger();
        InitializeComponent();
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _refreshTimer.Tick += RefreshTimer_Tick;
        Loaded += AccountManagerWindow_Loaded;
        Closing += AccountManagerWindow_Closing;
        Closed += (_, _) => DisposeResources();
        AccountWorkspaceManager.Changed += WorkspaceManager_Changed;
        System.Windows.Application.Current.DispatcherUnhandledException += Application_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        InitializeTrayIcon();
    }

    private async void AccountManagerWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshRows();
        UpdateResourceStatistics();
        ReadLogUpdates(initialLoad: true);
        await RefreshExceptionsAsync();
        _refreshTimer.Start();
        CenterStatusText.Text = "管理中心已启动；账号不会自动登录，请选择账号后手动点击“登录 / 打开工作区”";
    }

    internal void ShowCenter()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        UpdateResourceStatistics();
        ReadLogUpdates();
        var now = DateTime.Now;
        if (now >= _nextAccountRefreshAt)
        {
            _nextAccountRefreshAt = now.AddSeconds(5);
            RefreshRows();
        }
        if (now >= _nextExceptionRefreshAt)
        {
            _nextExceptionRefreshAt = now.AddSeconds(10);
            await RefreshExceptionsAsync();
        }
    }

    private void WorkspaceManager_Changed()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(WorkspaceManager_Changed);
            return;
        }
        RefreshRows();
        RebuildTrayMenu();
    }

    private void RefreshRows()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshRows);
            return;
        }
        var selectedUserId = (AccountsGrid.SelectedItem as AccountRow)?.UserId;
        var rows = _store.Load().Accounts.Values
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.LocalName)
            .Select(x =>
            {
                var running = AccountWorkspaceManager.IsRunning(x.UserId);
                var online = AccountWorkspaceManager.IsOnline(x.UserId);
                var failed = AccountWorkspaceManager.IsConnectionFailed(x.UserId);
                var occupied = !running && _store.IsSessionInUse(x.PhoneNumber);
                return new AccountRow(
                    x.UserId,
                    string.IsNullOrWhiteSpace(x.LocalName) ? x.DisplayName : x.LocalName,
                    x.DisplayName,
                    MaskPhone(x.PhoneNumber),
                    online ? "在线" : failed ? "连接失败，请重新登录" : running ? "连接中/需要登录" : occupied ? "其他实例运行中" : "已停止",
                    x.AutoStart,
                    running ? "打开" : occupied ? "已占用" : "登录",
                    !occupied);
            })
            .ToArray();
        AccountsGrid.ItemsSource = rows;
        if (selectedUserId is not null)
            AccountsGrid.SelectedItem = rows.FirstOrDefault(x => x.UserId == selectedUserId);
    }

    private AccountRow Selected() => AccountsGrid.SelectedItem as AccountRow
        ?? throw new InvalidOperationException("请先选择一个账户。");

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddAccountWindow { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Request is not null)
            AccountWorkspaceManager.OpenNew(dialog.Request, activate: true);
    }

    private void Open_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var row = Selected();
        if (!row.CanOpen)
            throw new InvalidOperationException(
                "该账户正在另一个 Telegram 控制台进程中运行。请使用原窗口，或正常退出旧版本后再登录。");
        if (AccountWorkspaceManager.IsRunning(row.UserId)) AccountWorkspaceManager.Show(row.UserId);
        else AccountWorkspaceManager.Open(_store.Load().Accounts[row.UserId]);
    });

    private void RowOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: long userId }) return;
        AccountsGrid.SelectedItem = AccountsGrid.Items.OfType<AccountRow>().FirstOrDefault(x => x.UserId == userId);
        Open_Click(sender, e);
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => Run(() => AccountWorkspaceManager.Stop(Selected().UserId));

    private void Remove_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var row = Selected();
        if (AccountWorkspaceManager.IsRunning(row.UserId))
            throw new InvalidOperationException("请先停止该账户，再从列表移除。");
        if (System.Windows.MessageBox.Show(this, $"从应用列表移除“{row.LocalName}”？\n本操作不会主动注销 Telegram Session。",
                "确认移除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _store.RemoveAccount(row.UserId);
        RefreshRows();
    });

    private void Tile_Click(object sender, RoutedEventArgs e) => AccountWorkspaceManager.TileAll();
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRows();
    private void AccountsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Open_Click(sender, e);

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_settings, _store) { Owner = this };
        if (window.ShowDialog() == true)
            CenterStatusText.Text = "全局设置已保存；代理变更将在账号重新连接时生效";
    }

    private async void RefreshExceptions_Click(object sender, RoutedEventArgs e) => await RefreshExceptionsAsync();

    private async Task RefreshExceptionsAsync()
    {
        try
        {
            var accounts = _store.Load().Accounts;
            var records = await _exceptionQuery.GetRecentAsync(300);
            ExceptionsGrid.ItemsSource = records.Select(x => new ExceptionRow(
                x.Id,
                x.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                accounts.TryGetValue(x.AccountId, out var account)
                    ? string.IsNullOrWhiteSpace(account.LocalName) ? account.DisplayName : account.LocalName
                    : x.AccountId == 0 ? "系统" : $"账号 {x.AccountId}",
                x.Level.ToString(),
                x.Category,
                x.Message,
                x.TelegramStatus,
                x.EmailStatus)).ToArray();
            ExceptionSummaryText.Text = $"最近 {records.Count} 条异常，可在账号工作区配置通知目标";
        }
        catch (Exception ex)
        {
            _logger.Warning("ManagementCenter", "刷新异常中心失败", ex);
            ExceptionSummaryText.Text = $"异常加载失败：{UserMessageFormatter.From(ex)}";
        }
    }

    private void OpenDataDirectory_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(_store.DataDirectory) { UseShellExecute = true });

    private void OpenLogDirectory_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(_logger.LogDirectory) { UseShellExecute = true });

    private void ClearLogs_Click(object sender, RoutedEventArgs e) => GlobalLogConsole.ClearOutput();

    private void UpdateResourceStatistics()
    {
        try
        {
            var snapshot = _resourceMonitor.Capture();
            ResourceMemoryText.Text = FormatBytes(snapshot.WorkingSetBytes);
            ResourceMemoryDetailText.Text = $"专用 {FormatBytes(snapshot.PrivateMemoryBytes)} · 托管堆 {FormatBytes(snapshot.ManagedHeapBytes)}";
            ResourceStorageText.Text = FormatBytes(snapshot.DataDirectoryBytes);
            ResourceStorageDetailText.Text = $"媒体缓存 {FormatBytes(snapshot.MediaCacheBytes)}";
            ResourceDiskText.Text = $"读 ↓ {FormatBytes(snapshot.DiskReadBytes)} · 写 ↑ {FormatBytes(snapshot.DiskWriteBytes)}";
            ResourceDiskDetailText.Text = $"当前 ↓ {FormatRate(snapshot.DiskReadBytesPerSecond)} · ↑ {FormatRate(snapshot.DiskWriteBytesPerSecond)}";
            ResourceNetworkText.Text = $"下载 ↓ {FormatBytes(snapshot.DownloadedBytes)} · 上传 ↑ {FormatBytes(snapshot.UploadedBytes)}";
            ResourceNetworkDetailText.Text = $"当前 ↓ {FormatRate(snapshot.DownloadBytesPerSecond)} · ↑ {FormatRate(snapshot.UploadBytesPerSecond)} · 运行 {FormatUptime(snapshot.Uptime)}";
        }
        catch (Exception ex)
        {
            _logger.Warning("ManagementCenter", "资源统计刷新失败", ex);
        }
    }

    private void ReadLogUpdates(bool initialLoad = false)
    {
        try
        {
            var path = Path.Combine(_logger.LogDirectory, $"telegram-{DateTime.Now:yyyy-MM-dd}.log");
            if (!File.Exists(path)) return;
            if (!string.Equals(path, _currentLogPath, StringComparison.OrdinalIgnoreCase))
            {
                _currentLogPath = path;
                _logPosition = 0;
                _logRemainder = "";
                _initialLogLoaded = false;
            }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < _logPosition) _logPosition = 0;
            if ((initialLoad || !_initialLogLoaded) && _logPosition == 0)
                _logPosition = Math.Max(0, stream.Length - 256 * 1024);
            var firstRead = !_initialLogLoaded;
            var readStartedAt = _logPosition;
            stream.Position = readStartedAt;
            var buffer = new byte[(int)Math.Min(512 * 1024, stream.Length - stream.Position)];
            var read = 0;
            while (read < buffer.Length)
            {
                var count = stream.Read(buffer, read, buffer.Length - read);
                if (count == 0) break;
                read += count;
            }
            _logPosition = stream.Position;
            _initialLogLoaded = true;
            if (read == 0) return;
            var text = _logRemainder + Encoding.UTF8.GetString(buffer, 0, read);
            var lines = text.Replace("\r\n", "\n").Split('\n');
            _logRemainder = lines[^1];
            var start = firstRead && readStartedAt > 0 ? 1 : 0;
            for (var index = start; index < lines.Length - 1; index++)
            {
                var line = lines[index];
                if (line.Length == 0) continue;
                GlobalLogConsole.AppendLine(line, LogColor(line));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static Brush LogColor(string line) =>
        line.Contains("[ERROR", StringComparison.OrdinalIgnoreCase) || line.Contains("[FATAL", StringComparison.OrdinalIgnoreCase)
            ? Brushes.OrangeRed
            : line.Contains("[WARN", StringComparison.OrdinalIgnoreCase) ? Brushes.Gold : Brushes.White;

    private void InitializeTrayIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/AppIcon.ico"))
                       ?? throw new InvalidOperationException("找不到应用图标资源");
        using var sourceIcon = new System.Drawing.Icon(resource.Stream);
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = (System.Drawing.Icon)sourceIcon.Clone(),
            Text = "TelegramConsole 管理中心",
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowCenter);
        RebuildTrayMenu();
    }

    private void RebuildTrayMenu()
    {
        if (_trayIcon is null) return;
        var oldMenu = _trayIcon.ContextMenuStrip;
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("TelegramConsole 管理中心").Enabled = false;
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("显示管理中心", null, (_, _) => Dispatcher.BeginInvoke(ShowCenter));
        foreach (var workspace in AccountWorkspaceManager.RunningWorkspaces())
        {
            var label = $"{(workspace.IsOnline ? "●" : "○")} {workspace.DisplayName}";
            menu.Items.Add(label, null, (_, _) => Dispatcher.BeginInvoke(workspace.Window.ShowWorkspace));
        }
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出应用", null, (_, _) => Dispatcher.BeginInvoke(ExitApplication));
        _trayIcon.ContextMenuStrip = menu;
        oldMenu?.Dispose();
    }

    private void AccountManagerWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exitRequested) return;
        e.Cancel = true;
        ShowInTaskbar = false;
        Hide();
        _trayIcon?.ShowBalloonTip(1800, "TelegramConsole 仍在运行", "账号和定时任务继续运行，双击托盘图标可恢复管理中心。", System.Windows.Forms.ToolTipIcon.Info);
    }

    private void Application_DispatcherUnhandledException(
        object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        _logger.Error("Application", "管理中心捕获未处理的界面异常", e.Exception);
        CenterStatusText.Text = $"界面异常：{UserMessageFormatter.From(e.Exception)}";
        e.Handled = true;
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger.Error("Application", "管理中心捕获未观察的后台任务异常", e.Exception);
        e.SetObserved();
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            _logger.Error("Application", "进程发生未处理异常", exception);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => ExitApplication();

    private void ExitApplication()
    {
        if (System.Windows.MessageBox.Show(this, "退出后将停止所有账号、监控和定时任务。确定退出？",
                "退出应用", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _exitRequested = true;
        AccountWorkspaceManager.StopAll();
        DisposeResources();
        System.Windows.Application.Current.Shutdown();
    }

    private void DisposeResources()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer.Stop();
        AccountWorkspaceManager.Changed -= WorkspaceManager_Changed;
        System.Windows.Application.Current.DispatcherUnhandledException -= Application_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
        _resourceMonitor.Dispose();
        _logger.Dispose();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.ContextMenuStrip?.Dispose();
            _trayIcon.Icon?.Dispose();
            _trayIcon.Dispose();
            _trayIcon = null;
        }
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) { System.Windows.MessageBox.Show(this, ex.Message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private static string MaskPhone(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return digits.Length < 7 ? phone : $"+{digits[..Math.Min(3, digits.Length - 4)]}****{digits[^4..]}";
    }

    private static string FormatRate(double bytesPerSecond) => $"{FormatBytes(bytesPerSecond)}/s";

    private static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }

    private static string FormatUptime(TimeSpan uptime) => uptime.TotalDays >= 1
        ? $"{(int)uptime.TotalDays}d {uptime:hh\\:mm\\:ss}"
        : uptime.ToString(@"hh\:mm\:ss");

    private sealed record AccountRow(
        long UserId, string LocalName, string DisplayName, string MaskedPhone, string Status, bool AutoStart,
        string ActionText, bool CanOpen);

    private sealed record ExceptionRow(
        long Id, string Time, string Account, string Level, string Category, string Message,
        string TelegramStatus, string EmailStatus);
}
