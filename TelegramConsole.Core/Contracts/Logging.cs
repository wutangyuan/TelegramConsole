namespace TelegramConsole.Core;

public enum AppLogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical
}

public sealed record AppLogEntry(
    DateTimeOffset Timestamp,
    AppLogLevel Level,
    string Category,
    string Message,
    string? Exception = null);

public sealed record ExceptionRecord(
    long Id,
    DateTimeOffset OccurredAt,
    AppLogLevel Level,
    string Category,
    string Message,
    string Details,
    string TelegramStatus,
    string EmailStatus);

public sealed record ExceptionQuery(
    DateTimeOffset? From = null,
    DateTimeOffset? ToExclusive = null,
    AppLogLevel? Level = null,
    string Keyword = "",
    int Limit = 500);

public interface IAppLogger : IDisposable
{
    string LogDirectory { get; }
    event Action<AppLogEntry>? EntryWritten;
    void Write(AppLogLevel level, string category, string message, Exception? exception = null);
}

public interface IExceptionMonitorService : IDisposable
{
    string DatabasePath { get; }
    event Action? RecordsChanged;
    void ActivateAccount(long userId, ExceptionAlertSettings settings);
    void DeactivateAccount();
    Task<IReadOnlyList<ExceptionRecord>> GetRecentAsync(int limit = 200);
    Task<IReadOnlyList<ExceptionRecord>> QueryAsync(ExceptionQuery query);
    Task RetryNotificationsAsync(IEnumerable<long> recordIds);
    Task SendTestNotificationAsync();
}

public sealed record GlobalExceptionRecord(
    long Id,
    long AccountId,
    DateTimeOffset OccurredAt,
    AppLogLevel Level,
    string Category,
    string Message,
    string Details,
    string TelegramStatus,
    string EmailStatus);

public interface IExceptionLogQueryService
{
    string DatabasePath { get; }
    Task<IReadOnlyList<GlobalExceptionRecord>> GetRecentAsync(int limit = 200);
}

public sealed record MentionRecord(
    long Id,
    DateTimeOffset OccurredAt,
    string ChatName,
    string Sender,
    string Message,
    string NotificationStatus);

public sealed record MentionQuery(string Keyword = "", int Limit = 500);

public interface IMentionMonitorService : IDisposable
{
    string DatabasePath { get; }
    event Action? RecordsChanged;
    void ActivateAccount(long userId, MentionAlertSettings settings);
    void DeactivateAccount();
    Task<IReadOnlyList<MentionRecord>> QueryAsync(MentionQuery query);
    Task SendTestNotificationAsync();
}

public static class AppLoggerExtensions
{
    public static void Info(this IAppLogger logger, string category, string message) =>
        logger.Write(AppLogLevel.Information, category, message);

    public static void Warning(this IAppLogger logger, string category, string message, Exception? exception = null) =>
        logger.Write(AppLogLevel.Warning, category, message, exception);

    public static void Error(this IAppLogger logger, string category, string message, Exception? exception = null) =>
        logger.Write(AppLogLevel.Error, category, message, exception);
}
