using System.Runtime.InteropServices;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;

namespace TelegramConsoleApp;

internal static class ClipboardHelper
{
    private const uint GmemMoveable = 0x0002;
    private const uint CfUnicodeText = 13;
    private static readonly SemaphoreSlim TextClipboardLock = new(1, 1);

    public static async Task<bool> TrySetTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        await TextClipboardLock.WaitAsync();
        try
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                if (await Task.Run(() => TrySetNativeText(text))) return true;
                await Task.Delay(20 * (attempt + 1));
            }
            return false;
        }
        finally
        {
            TextClipboardLock.Release();
        }
    }

    private static bool TrySetNativeText(string text)
    {
        if (!OpenClipboard(IntPtr.Zero)) return false;
        IntPtr memory = IntPtr.Zero;
        try
        {
            if (!EmptyClipboard()) return false;
            var bytes = Encoding.Unicode.GetBytes(text + '\0');
            memory = GlobalAlloc(GmemMoveable, (UIntPtr)bytes.Length);
            if (memory == IntPtr.Zero) return false;
            var destination = GlobalLock(memory);
            if (destination == IntPtr.Zero) return false;
            try
            {
                Marshal.Copy(bytes, 0, destination, bytes.Length);
            }
            finally
            {
                GlobalUnlock(memory);
            }

            if (SetClipboardData(CfUnicodeText, memory) == IntPtr.Zero) return false;
            memory = IntPtr.Zero; // Ownership was transferred to Windows.
            return true;
        }
        finally
        {
            if (memory != IntPtr.Zero) GlobalFree(memory);
            CloseClipboard();
        }
    }

    public static async Task<bool> TrySetMediaAsync(string path, bool copyAsImage)
    {
        if (!File.Exists(path)) return false;
        if (copyAsImage)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                if (await TrySetAsync(() => System.Windows.Clipboard.SetImage(image))) return true;
            }
            catch
            {
                // Unsupported image formats (for example some stickers) are copied as files.
            }
        }

        return await TrySetAsync(() =>
        {
            var data = new System.Windows.DataObject();
            data.SetData(System.Windows.DataFormats.FileDrop, new[] { Path.GetFullPath(path) });
            System.Windows.Clipboard.SetDataObject(data, copy: false);
        });
    }

    private static async Task<bool> TrySetAsync(Action write)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                write();
                return true;
            }
            catch (ExternalException) when (attempt < 4)
            {
                await Task.Delay(20 * (attempt + 1));
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);
}
