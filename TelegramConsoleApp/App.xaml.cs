using System.Windows;

namespace TelegramConsoleApp;

public partial class App : Application
{
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
    }
}
