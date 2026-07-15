namespace TelegramConsole.Core;

public interface ISettingsStore
{
    string DataDirectory { get; }
    string SessionPath { get; }
    string GetSessionPath(string phoneNumber);
    AppSettings Load();
    void Save(AppSettings settings);
}

public interface ITelegramService : IDisposable
{
    bool IsLoggedIn { get; }
    long CurrentUserId { get; }
    string CurrentUser { get; }
    event Action<ChatLine>? MessageReceived;
    event Action<string>? Log;
    Task<string?> BeginLoginAsync(AppSettings settings);
    Task<string?> ContinueLoginAsync(string value);
    Task<List<DialogItem>> LoadDialogsAsync();
    Task<List<ChatLine>> LoadHistoryAsync(DialogItem dialog, int limit = 50);
    Task SendAsync(DialogItem dialog, string text);
    Task SendScheduledAsync(ScheduledMessage schedule);
    Task SendConfirmationAsync(ScheduledMessage schedule, string text);
}

public interface ISchedulerService : IDisposable
{
    event Action<string>? Status;
    Task ActivateAccountAsync(AccountProfile account);
    Task DeactivateAccountAsync();
    Task UpsertAsync(ScheduledMessage task);
    Task DeleteAsync(Guid taskId);
    Task RunDueTasksAsync();
    Task ExecuteNowAsync(Guid taskId);
}
