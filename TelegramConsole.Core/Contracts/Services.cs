namespace TelegramConsole.Core;

public interface ISettingsStore
{
    string DataDirectory { get; }
    string SessionPath { get; }
    string GetSessionPath(string phoneNumber);
    bool IsSessionInUse(string phoneNumber);
    AppSettings Load();
    void Save(AppSettings settings);
    void SaveAccount(AccountProfile account);
    void RemoveAccount(long userId);
}

public interface IManagedAccountCatalog
{
    IReadOnlyList<ManagedAccountDefinition> Load();
    void Save(ManagedAccountDefinition account);
    void Remove(Guid accountId);
}

public interface IApplicationResourceMonitor : IDisposable
{
    ApplicationResourceSnapshot Capture();
}

public interface ITelegramService : IDisposable
{
    bool IsLoggedIn { get; }
    long CurrentUserId { get; }
    string CurrentUser { get; }
    event Action<ChatLine>? MessageReceived;
    event Action<MessageDeletion>? MessageDeleted;
    event Action<string>? Log;
    event Action<TelegramConnectionState>? ConnectionStateChanged;
    event Action? OutboxChanged;
    event Action<string>? AutomationActivity;
    string OutboxDatabasePath { get; }
    Task<string?> BeginLoginAsync(AppSettings settings);
    Task<string?> ContinueLoginAsync(string value);
    Task<List<DialogItem>> LoadDialogsAsync();
    Task<List<ChatLine>> LoadHistoryAsync(DialogItem dialog, int limit = 300);
    Task<string> DownloadMediaAsync(DialogItem dialog, int messageId);
    Task<string?> DownloadMediaThumbnailAsync(DialogItem dialog, int messageId);
    Task SendAsync(DialogItem dialog, string text);
    Task SendScheduledAsync(ScheduledMessage schedule);
    Task SendConfirmationAsync(ScheduledMessage schedule, string text);
    Task<IReadOnlyList<OutgoingMessageRecord>> QueryOutboxAsync(int limit = 200);
    Task RetryOutboxAsync(long recordId);
    void ConfigureAutomationRules(IReadOnlyList<AutomationRule> rules);
    Task<IReadOnlyList<MessageSearchResult>> SearchMessagesAsync(string query, DialogItem? dialog = null, int limit = 100);
    Task<IReadOnlyList<ForumTopicItem>> LoadForumTopicsAsync(DialogItem dialog);
    Task<ServerScheduledMessage> ScheduleServerMessageAsync(DialogItem dialog, string text, DateTime sendAt);
    Task<IReadOnlyList<ServerScheduledMessage>> LoadServerScheduledMessagesAsync(DialogItem dialog);
    Task DeleteServerScheduledMessagesAsync(DialogItem dialog, IReadOnlyCollection<int> messageIds);
    Task SendReplyAsync(DialogItem dialog, int replyToMessageId, string text, string quote = "");
    Task EditMessageAsync(DialogItem dialog, int messageId, string text);
    Task DeleteMessagesAsync(DialogItem dialog, IReadOnlyCollection<int> messageIds, bool revoke = true);
    Task<IReadOnlyList<string>> LoadAvailableReactionsAsync(DialogItem dialog);
    Task SendReactionAsync(DialogItem dialog, int messageId, string emoji);
    Task ForwardMessagesAsync(DialogItem source, IReadOnlyCollection<int> messageIds, DialogItem target);
    string GetMessageLink(DialogItem dialog, int messageId);
    Task SaveCloudDraftAsync(DialogItem dialog, string text, int? replyToMessageId = null);
    Task<string> LoadCloudDraftAsync(DialogItem dialog);
    Task<IReadOnlyList<DialogFolderItem>> LoadDialogFoldersAsync();
    Task CreateDialogFolderAsync(string title, IReadOnlyCollection<DialogItem> dialogs);
}

public enum TelegramConnectionStatus
{
    Connecting,
    Recovering,
    Connected,
    Disconnected
}

public sealed record TelegramConnectionState(TelegramConnectionStatus Status, string Message);

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

public interface IIntervalChatAutomationService : IDisposable
{
    event Action<string>? Status;
    Task ActivateAccountAsync(AccountProfile account);
    Task DeactivateAccountAsync();
    Task UpsertAsync(IntervalChatRule rule);
    Task DeleteAsync(Guid ruleId);
    Task ExecuteNowAsync(Guid ruleId);
}
