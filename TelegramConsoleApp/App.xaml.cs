using System.Windows;

namespace TelegramConsoleApp;

public partial class App : Application
{
    private AccountManagerWindow? _managementCenter;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--startup-check", StringComparer.OrdinalIgnoreCase))
        {
            base.OnStartup(e);
            Shutdown(0);
            return;
        }
        var settings = new SettingsStore().Load();
        LocalizationManager.ApplyLanguage(settings.Language);
        base.OnStartup(e);
        _managementCenter = new AccountManagerWindow(new SettingsStore());
        MainWindow = _managementCenter;
        _managementCenter.Show();
    }

    internal void ShowManagementCenter()
    {
        if (_managementCenter is null) return;
        _managementCenter.ShowCenter();
    }
}
