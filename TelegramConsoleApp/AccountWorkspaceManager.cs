using System.Windows;

namespace TelegramConsoleApp;

internal static class AccountWorkspaceManager
{
    private static readonly object Sync = new();
    private static readonly HashSet<MainWindow> Windows = [];
    private static readonly Dictionary<long, MainWindow> Accounts = [];

    public static event Action? Changed;

    public static void Register(MainWindow window, long userId = 0)
    {
        lock (Sync)
        {
            Windows.Add(window);
            foreach (var stale in Accounts.Where(x => ReferenceEquals(x.Value, window)).Select(x => x.Key).ToArray())
                Accounts.Remove(stale);
            if (userId != 0) Accounts[userId] = window;
        }
        NotifyChanged();
    }

    public static void Unregister(MainWindow window)
    {
        lock (Sync)
        {
            Windows.Remove(window);
            foreach (var stale in Accounts.Where(x => ReferenceEquals(x.Value, window)).Select(x => x.Key).ToArray())
                Accounts.Remove(stale);
        }
        NotifyChanged();
    }

    public static void Open(AccountProfile account, bool activate = true)
    {
        MainWindow? existing;
        lock (Sync) Accounts.TryGetValue(account.UserId, out existing);
        if (existing is not null)
        {
            if (activate) existing.ShowWorkspace();
            return;
        }

        var request = new AccountLaunchRequest(
            string.IsNullOrWhiteSpace(account.LocalName) ? account.DisplayName : account.LocalName,
            account.PhoneNumber,
            AutoLogin: true);
        OpenNew(request, activate, account.UserId);
    }

    public static void OpenNew(AccountLaunchRequest request, bool activate = true, long expectedUserId = 0)
    {
        MainWindow? duplicate;
        lock (Sync)
            duplicate = Windows.FirstOrDefault(x =>
                string.Equals(x.PhoneNumber, request.PhoneNumber, StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null)
        {
            if (request.AutoLogin) duplicate.StartLoginFromManager(activate);
            else if (activate) duplicate.ShowWorkspace();
            return;
        }

        var window = new MainWindow(request, managedWorkspace: true);
        Register(window, expectedUserId);
        if (activate)
        {
            window.Show();
        }
        else
        {
            // Auto-start the account services without ever making the workspace
            // visible. Show()+Hide() caused one startup flash per account.
            window.StartWorkspaceInBackground();
        }
    }

    public static bool IsRunning(long userId)
    {
        lock (Sync) return Accounts.ContainsKey(userId);
    }

    public static bool IsOnline(long userId)
    {
        lock (Sync) return Accounts.TryGetValue(userId, out var window) && window.IsWorkspaceOnline;
    }

    public static IReadOnlyList<RunningWorkspace> RunningWorkspaces()
    {
        lock (Sync)
            return Windows
                .Select(x => new RunningWorkspace(
                    x.AccountUserId,
                    x.WorkspaceDisplayName,
                    x.IsWorkspaceOnline,
                    x))
                .OrderBy(x => x.DisplayName)
                .ToArray();
    }

    public static void Show(long userId)
    {
        MainWindow? window;
        lock (Sync) Accounts.TryGetValue(userId, out window);
        if (window is null) return;
        // Opening an existing workspace must not start a second login while the
        // first login is awaiting verification or the client is recovering.
        window.ShowWorkspace();
    }

    public static void Stop(long userId)
    {
        MainWindow? window;
        lock (Sync) Accounts.TryGetValue(userId, out window);
        window?.StopWorkspace();
    }

    public static void StopAll(MainWindow? except = null)
    {
        MainWindow[] windows;
        lock (Sync) windows = Windows.Where(x => !ReferenceEquals(x, except)).ToArray();
        foreach (var window in windows) window.StopWorkspace();
    }

    public static void TileAll()
    {
        MainWindow[] windows;
        lock (Sync) windows = Windows.ToArray();
        if (windows.Length == 0) return;
        var area = SystemParameters.WorkArea;
        var columns = (int)Math.Ceiling(Math.Sqrt(windows.Length));
        var rows = (int)Math.Ceiling(windows.Length / (double)columns);
        var width = Math.Max(640, area.Width / columns);
        var height = Math.Max(500, area.Height / rows);
        for (var index = 0; index < windows.Length; index++)
        {
            var window = windows[index];
            window.ShowWorkspace();
            window.WindowState = WindowState.Normal;
            window.Left = area.Left + index % columns * width;
            window.Top = area.Top + index / columns * height;
            window.Width = Math.Min(width, area.Right - window.Left);
            window.Height = Math.Min(height, area.Bottom - window.Top);
        }
    }

    public static void NotifyChanged() =>
        Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());

    internal sealed record RunningWorkspace(long UserId, string DisplayName, bool IsOnline, MainWindow Window);
}
