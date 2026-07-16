using System.Windows;
using System.Windows.Input;

namespace TelegramConsoleApp;

public partial class AccountManagerWindow : Window
{
    private readonly ISettingsStore _store;

    public AccountManagerWindow(ISettingsStore store)
    {
        _store = store;
        InitializeComponent();
        Loaded += (_, _) => RefreshRows();
        Closed += (_, _) => AccountWorkspaceManager.Changed -= RefreshRows;
        AccountWorkspaceManager.Changed += RefreshRows;
    }

    private void RefreshRows()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshRows);
            return;
        }
        AccountsGrid.ItemsSource = _store.Load().Accounts.Values
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.LocalName)
            .Select(x =>
            {
                var running = AccountWorkspaceManager.IsRunning(x.UserId);
                var online = AccountWorkspaceManager.IsOnline(x.UserId);
                var occupied = !running && _store.IsSessionInUse(x.PhoneNumber);
                return new AccountRow(
                    x.UserId,
                    string.IsNullOrWhiteSpace(x.LocalName) ? x.DisplayName : x.LocalName,
                    x.DisplayName,
                    MaskPhone(x.PhoneNumber),
                    online ? "在线" : running ? "连接中/需要登录" : occupied ? "其他实例运行中" : "已停止",
                    x.AutoStart,
                    running ? "打开" : occupied ? "已占用" : "登录",
                    !occupied);
            })
            .ToArray();
    }

    private AccountRow Selected() => AccountsGrid.SelectedItem as AccountRow
        ?? throw new InvalidOperationException("请先选择一个账户。");

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddAccountWindow { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Request is not null)
            AccountWorkspaceManager.OpenNew(dialog.Request);
    }

    private void Open_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var row = Selected();
        if (!row.CanOpen)
            throw new InvalidOperationException(
                "该账户正在另一个 Telegram 控制台进程中运行。请使用原窗口，或正常退出旧版本后再登录。");
        if (AccountWorkspaceManager.IsRunning(row.UserId)) AccountWorkspaceManager.Show(row.UserId);
        else
        {
            var account = _store.Load().Accounts[row.UserId];
            AccountWorkspaceManager.Open(account);
        }
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

    private sealed record AccountRow(
        long UserId, string LocalName, string DisplayName, string MaskedPhone, string Status, bool AutoStart,
        string ActionText, bool CanOpen);
}
