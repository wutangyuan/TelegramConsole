using System.Diagnostics;
using System.IO;
using System.Windows;

namespace TelegramConsoleApp;

internal static class MediaFileLauncher
{
    private static readonly HashSet<string> DangerousExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".com", ".scr", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".jse",
        ".wsf", ".wsh", ".reg", ".lnk", ".url", ".hta"
    };

    public static bool Open(Window owner, string path)
    {
        var extension = Path.GetExtension(path);
        if (DangerousExtensions.Contains(extension))
        {
            var answer = System.Windows.MessageBox.Show(
                owner,
                LocalizationManager.Format("DangerousMediaConfirm", Path.GetFileName(path)),
                LocalizationManager.Text("SecurityWarning"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return false;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return true;
    }
}
