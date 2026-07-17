using System.Diagnostics;
using System.Runtime.InteropServices;
using TelegramConsole.Core;

namespace TelegramConsole.Infrastructure;

public sealed class ApplicationResourceMonitor : IApplicationResourceMonitor
{
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly string _dataDirectory;
    private readonly object _sync = new();
    private DateTime _lastCaptureUtc = DateTime.UtcNow;
    private DateTime _lastStorageScanUtc = DateTime.MinValue;
    private ulong _lastDiskReadBytes;
    private ulong _lastDiskWriteBytes;
    private long _lastUploadedBytes;
    private long _lastDownloadedBytes;
    private long _dataDirectoryBytes;
    private long _mediaCacheBytes;
    private bool _disposed;

    public ApplicationResourceMonitor(string dataDirectory)
    {
        _dataDirectory = dataDirectory;
        if (TryGetIoCounters(out var counters))
        {
            _lastDiskReadBytes = counters.ReadTransferCount;
            _lastDiskWriteBytes = counters.WriteTransferCount;
        }
        var traffic = TelegramTrafficMeter.Snapshot;
        _lastUploadedBytes = traffic.UploadedBytes;
        _lastDownloadedBytes = traffic.DownloadedBytes;
    }

    public ApplicationResourceSnapshot Capture()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = DateTime.UtcNow;
            var elapsed = Math.Max(0.1, (now - _lastCaptureUtc).TotalSeconds);
            _lastCaptureUtc = now;
            _process.Refresh();

            ulong diskRead = _lastDiskReadBytes;
            ulong diskWrite = _lastDiskWriteBytes;
            if (TryGetIoCounters(out var counters))
            {
                diskRead = counters.ReadTransferCount;
                diskWrite = counters.WriteTransferCount;
            }
            var diskReadRate = Delta(diskRead, _lastDiskReadBytes) / elapsed;
            var diskWriteRate = Delta(diskWrite, _lastDiskWriteBytes) / elapsed;
            _lastDiskReadBytes = diskRead;
            _lastDiskWriteBytes = diskWrite;

            var traffic = TelegramTrafficMeter.Snapshot;
            var uploadRate = Math.Max(0, traffic.UploadedBytes - _lastUploadedBytes) / elapsed;
            var downloadRate = Math.Max(0, traffic.DownloadedBytes - _lastDownloadedBytes) / elapsed;
            _lastUploadedBytes = traffic.UploadedBytes;
            _lastDownloadedBytes = traffic.DownloadedBytes;

            if (now - _lastStorageScanUtc >= TimeSpan.FromSeconds(15))
            {
                _lastStorageScanUtc = now;
                _dataDirectoryBytes = DirectorySize(_dataDirectory);
                _mediaCacheBytes = DirectorySize(Path.Combine(_dataDirectory, "media"));
            }

            return new ApplicationResourceSnapshot(
                _process.WorkingSet64,
                _process.PrivateMemorySize64,
                GC.GetTotalMemory(forceFullCollection: false),
                diskRead,
                diskWrite,
                diskReadRate,
                diskWriteRate,
                traffic.UploadedBytes,
                traffic.DownloadedBytes,
                uploadRate,
                downloadRate,
                _dataDirectoryBytes,
                _mediaCacheBytes,
                DateTime.Now - _process.StartTime,
                DateTimeOffset.Now);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _process.Dispose();
        }
    }

    private bool TryGetIoCounters(out IoCounters counters)
    {
        if (OperatingSystem.IsWindows())
            return GetProcessIoCounters(_process.Handle, out counters);
        if (OperatingSystem.IsLinux())
            return TryGetLinuxIoCounters(out counters);
        counters = default;
        return false;
    }

    private static bool TryGetLinuxIoCounters(out IoCounters counters)
    {
        counters = default;
        try
        {
            foreach (var line in File.ReadLines("/proc/self/io"))
            {
                var separator = line.IndexOf(':');
                if (separator <= 0 || !ulong.TryParse(line[(separator + 1)..].Trim(), out var value)) continue;
                switch (line[..separator])
                {
                    case "syscr": counters.ReadOperationCount = value; break;
                    case "syscw": counters.WriteOperationCount = value; break;
                    case "read_bytes": counters.ReadTransferCount = value; break;
                    case "write_bytes": counters.WriteTransferCount = value; break;
                }
            }
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static double Delta(ulong current, ulong previous) => current >= previous ? current - previous : 0;

    private static long DirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        try
        {
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return total;
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr processHandle, out IoCounters counters);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }
}
