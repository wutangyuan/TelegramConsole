using System.Text.RegularExpressions;
using log4net;
using log4net.Appender;
using log4net.Config;
using log4net.Layout;
using log4net.Repository;
using TelegramConsole.Core;

namespace TelegramConsole.Infrastructure;

public sealed partial class Log4NetAppLogger : IAppLogger
{
    private readonly ILoggerRepository _repository;
    private readonly ILog _log;
    private bool _disposed;

    public string LogDirectory { get; }
    public event Action<AppLogEntry>? EntryWritten;

    public Log4NetAppLogger(string? logDirectory = null)
    {
        LogDirectory = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TelegramConsoleApp", "logs");
        Directory.CreateDirectory(LogDirectory);
        DeleteExpiredFiles();

        _repository = LogManager.CreateRepository($"TelegramConsole.{Guid.NewGuid():N}");
        var appender = new RollingFileAppender
        {
            Name = "DailyRollingFile",
            File = Path.Combine(LogDirectory, "telegram-"),
            AppendToFile = true,
            RollingStyle = RollingFileAppender.RollingMode.Date,
            DatePattern = "yyyy-MM-dd'.log'",
            StaticLogFileName = false,
            ImmediateFlush = true,
            LockingModel = new FileAppender.MinimalLock(),
            Layout = new PatternLayout("%date{yyyy-MM-dd HH:mm:ss.fff zzz} [%-5level] %message%newline")
        };
        appender.ActivateOptions();
        BasicConfigurator.Configure(_repository, appender);
        _log = LogManager.GetLogger(_repository.Name, "TelegramConsole");

        Write(AppLogLevel.Information, "Application", "log4net 日志系统已启动");
    }

    public void Write(AppLogLevel level, string category, string message, Exception? exception = null)
    {
        if (_disposed) return;

        var safeEntry = new AppLogEntry(
            DateTimeOffset.Now,
            level,
            Sanitize(category),
            Sanitize(message),
            exception is null ? null : Sanitize($"{exception.GetType().Name}: {exception.Message}\n{exception.StackTrace}"));

        EntryWritten?.Invoke(safeEntry);
        var text = $"[{safeEntry.Category}] {safeEntry.Message}";
        if (!string.IsNullOrWhiteSpace(safeEntry.Exception))
            text += Environment.NewLine + safeEntry.Exception;

        switch (level)
        {
            case AppLogLevel.Trace:
            case AppLogLevel.Debug:
                _log.Debug(text);
                break;
            case AppLogLevel.Information:
                _log.Info(text);
                break;
            case AppLogLevel.Warning:
                _log.Warn(text);
                break;
            case AppLogLevel.Error:
                _log.Error(text);
                break;
            case AppLogLevel.Critical:
                _log.Fatal(text);
                break;
        }
    }

    private void DeleteExpiredFiles()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-30);
            foreach (var file in Directory.EnumerateFiles(LogDirectory, "*.log"))
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
        }
        catch
        {
            // Retention cleanup is best-effort and must not prevent startup.
        }
    }

    private static string Sanitize(string value)
    {
        value = ApiHashRegex().Replace(value, "[REDACTED_API_HASH]");
        value = LongHexRegex().Replace(value, "[REDACTED_KEY]");
        value = PhoneRegex().Replace(value, "[REDACTED_NUMBER]");
        value = ProxySecretRegex().Replace(value, "$1[REDACTED]");
        value = PasswordRegex().Replace(value, "$1[REDACTED]");
        return value;
    }

    [GeneratedRegex("(?i)(?<![a-f0-9])[a-f0-9]{32}(?![a-f0-9])")]
    private static partial Regex ApiHashRegex();

    [GeneratedRegex("(?i)(?<![a-f0-9])[a-f0-9]{40,}(?![a-f0-9])")]
    private static partial Regex LongHexRegex();

    [GeneratedRegex("(?<!\\d)\\+?\\d{10,15}(?!\\d)")]
    private static partial Regex PhoneRegex();

    [GeneratedRegex("(?i)(secret=)[^&\\s]+")]
    private static partial Regex ProxySecretRegex();

    [GeneratedRegex("(?i)(password|验证码|verification_code)(\\s*[:=]\\s*)[^\\s,;]+")]
    private static partial Regex PasswordRegex();

    public void Dispose()
    {
        if (_disposed) return;
        Write(AppLogLevel.Information, "Application", "log4net 日志系统正在关闭");
        _disposed = true;
        LogManager.ShutdownRepository(_repository.Name);
    }
}
