using System.Windows;

namespace TelegramConsoleApp;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var settings = new SettingsStore().Load();
        LocalizationManager.ApplyLanguage(settings.Language);
        base.OnStartup(e);
    }
}
