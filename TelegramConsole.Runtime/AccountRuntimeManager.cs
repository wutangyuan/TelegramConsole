using System.Collections.Concurrent;
using TelegramConsole.Core;
using TelegramConsole.Infrastructure;

namespace TelegramConsole.Runtime;

public sealed class AccountRuntimeManager : IAsyncDisposable
{
    private readonly IManagedAccountCatalog _catalog;
    private readonly ISettingsStore _settingsStore;
    private readonly string _dataDirectory;
    private readonly ConcurrentDictionary<Guid, AccountRuntime> _runtimes = new();
    private bool _disposed;

    public AccountRuntimeManager(IManagedAccountCatalog catalog, ISettingsStore settingsStore)
    {
        _catalog = catalog;
        _settingsStore = settingsStore;
        _dataDirectory = settingsStore.DataDirectory;
        foreach (var definition in catalog.Load())
            _runtimes.TryAdd(definition.Id, new AccountRuntime(definition, catalog, settingsStore, _dataDirectory));
    }

    public IReadOnlyList<AccountRuntimeSnapshot> GetSnapshots() =>
        _runtimes.Values.Select(x => x.Snapshot).OrderBy(x => x.LocalName).ToArray();

    public async Task StartAutoAccountsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var runtime in _runtimes.Values.Where(x => x.Definition.AutoStart))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { await runtime.StartAsync(cancellationToken); }
            catch { /* Runtime state and log preserve the individual startup failure. */ }
        }
    }

    public async Task<AccountRuntimeSnapshot> AddAsync(CreateManagedAccountRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (request.ApiId <= 0) throw new ArgumentException("API ID 必须大于 0");
        if (string.IsNullOrWhiteSpace(request.ApiHash)) throw new ArgumentException("API Hash 不能为空");
        if (string.IsNullOrWhiteSpace(request.PhoneNumber)) throw new ArgumentException("手机号不能为空");
        if (_runtimes.Values.Any(x => NormalizePhone(x.Definition.PhoneNumber) == NormalizePhone(request.PhoneNumber)))
            throw new InvalidOperationException("该手机号已经添加");

        var definition = new ManagedAccountDefinition
        {
            LocalName = string.IsNullOrWhiteSpace(request.LocalName) ? "新账户" : request.LocalName.Trim(),
            ApiId = request.ApiId,
            ApiHash = request.ApiHash.Trim(),
            PhoneNumber = request.PhoneNumber.Trim(),
            AutoStart = request.AutoStart,
            Proxy = request.Proxy ?? new ProxySettings()
        };
        _catalog.Save(definition);
        var runtime = new AccountRuntime(definition, _catalog, _settingsStore, _dataDirectory);
        if (!_runtimes.TryAdd(definition.Id, runtime)) throw new InvalidOperationException("账户运行器创建失败");
        await runtime.StartAsync(cancellationToken);
        return runtime.Snapshot;
    }

    public Task<AccountRuntimeSnapshot> StartAsync(Guid id, CancellationToken cancellationToken = default) =>
        Get(id).StartAsync(cancellationToken);

    public Task<AccountRuntimeSnapshot> ContinueLoginAsync(Guid id, string value, CancellationToken cancellationToken = default) =>
        Get(id).ContinueLoginAsync(value, cancellationToken);

    public async Task StopAsync(Guid id) => await Get(id).StopAsync();

    public async Task RemoveAsync(Guid id)
    {
        if (!_runtimes.TryRemove(id, out var runtime)) throw new KeyNotFoundException("账户不存在");
        await runtime.DisposeAsync();
        _catalog.Remove(id);
    }

    public Task<IReadOnlyList<DialogItem>> LoadDialogsAsync(Guid id) => Get(id).LoadDialogsAsync();
    public Task<IReadOnlyList<ChatLine>> LoadHistoryAsync(Guid id, DialogItem dialog, int limit = 300) =>
        Get(id).LoadHistoryAsync(dialog, limit);
    public IReadOnlyList<ChatLine> GetRecentMessages(Guid id, int limit = 300) => Get(id).GetRecentMessages(limit);
    public Task SendAsync(Guid id, SendChatMessageRequest request) => Get(id).SendAsync(request);
    public Task SendReplyAsync(Guid id, int messageId, SendChatMessageRequest request) =>
        Get(id).SendReplyAsync(messageId, request);
    public Task EditMessageAsync(Guid id, int messageId, SendChatMessageRequest request) =>
        Get(id).EditMessageAsync(messageId, request);
    public Task DeleteMessageAsync(Guid id, int messageId, DialogItem dialog) =>
        Get(id).DeleteMessageAsync(messageId, dialog);
    public Task<IReadOnlyList<string>> LoadAvailableReactionsAsync(Guid id, DialogItem dialog) =>
        Get(id).LoadAvailableReactionsAsync(dialog);
    public Task SendReactionAsync(Guid id, int messageId, DialogItem dialog, string emoji) =>
        Get(id).SendReactionAsync(messageId, dialog, emoji);
    public IReadOnlyList<ScheduledMessage> GetSchedules(Guid id) => Get(id).GetSchedules();
    public Task UpsertScheduleAsync(Guid id, ScheduledMessage schedule) => Get(id).UpsertScheduleAsync(schedule);
    public Task DeleteScheduleAsync(Guid id, Guid scheduleId) => Get(id).DeleteScheduleAsync(scheduleId);
    public Task ExecuteScheduleAsync(Guid id, Guid scheduleId) => Get(id).ExecuteScheduleAsync(scheduleId);
    public IReadOnlyList<IntervalChatRule> GetIntervalChatRules(Guid id) => Get(id).GetIntervalChatRules();
    public Task UpsertIntervalChatRuleAsync(Guid id, IntervalChatRule rule) => Get(id).UpsertIntervalChatRuleAsync(rule);
    public Task DeleteIntervalChatRuleAsync(Guid id, Guid ruleId) => Get(id).DeleteIntervalChatRuleAsync(ruleId);
    public Task ExecuteIntervalChatRuleAsync(Guid id, Guid ruleId) => Get(id).ExecuteIntervalChatRuleAsync(ruleId);
    public IReadOnlyList<AppLogEntry> GetRuntimeLogs(Guid id, AppLogLevel? level = null, string keyword = "", int limit = 300) =>
        Get(id).GetRuntimeLogs(level, keyword, limit);
    public Task<IReadOnlyList<ExceptionRecord>> QueryExceptionsAsync(Guid id, ExceptionQuery query) =>
        Get(id).QueryExceptionsAsync(query);
    public Task RetryExceptionNotificationsAsync(Guid id, IEnumerable<long> recordIds) =>
        Get(id).RetryExceptionNotificationsAsync(recordIds);
    public Task<IReadOnlyList<MentionRecord>> QueryMentionsAsync(Guid id, MentionQuery query) =>
        Get(id).QueryMentionsAsync(query);
    public Task<IReadOnlyList<OutgoingMessageRecord>> QueryOutboxAsync(Guid id, int limit = 200) =>
        Get(id).QueryOutboxAsync(limit);
    public Task RetryOutboxAsync(Guid id, long recordId) => Get(id).RetryOutboxAsync(recordId);
    public EmailSettings GetEmailSettings()
    {
        var email = _settingsStore.Load().Email;
        return CloneEmail(email);
    }

    public void SaveEmailSettings(EmailSettings email)
    {
        var settings = _settingsStore.Load();
        settings.Email = CloneEmail(email);
        _settingsStore.Save(settings);
        foreach (var runtime in _runtimes.Values) runtime.UpdateEmailSettings(email);
    }

    private static EmailSettings CloneEmail(EmailSettings email) => new()
    {
        SmtpHost = email.SmtpHost,
        SmtpPort = email.SmtpPort,
        EnableSsl = email.EnableSsl,
        UserName = email.UserName,
        Password = email.Password,
        FromAddress = email.FromAddress
    };

    private AccountRuntime Get(Guid id) =>
        _runtimes.TryGetValue(id, out var runtime) ? runtime : throw new KeyNotFoundException("账户不存在");

    private static string NormalizePhone(string value) => new(value.Where(char.IsDigit).ToArray());
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var runtime in _runtimes.Values) await runtime.DisposeAsync();
        _runtimes.Clear();
    }
}

internal sealed class AccountRuntime : IAsyncDisposable
{
    private readonly IManagedAccountCatalog _catalog;
    private readonly ISettingsStore _store;
    private readonly string _dataDirectory;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly List<ChatLine> _messages = [];
    private readonly object _messagesSync = new();
    private readonly Queue<AppLogEntry> _logs = new();
    private readonly object _logsSync = new();
    private TelegramService? _telegram;
    private SchedulerService? _scheduler;
    private ExceptionMonitorService? _exceptionMonitor;
    private MentionMonitorService? _mentionMonitor;
    private IntervalChatAutomationService? _intervalChatAutomation;
    private Log4NetAppLogger? _logger;
    private AccountProfile? _profile;
    private AppSettings? _runtimeSettings;
    private AccountRuntimeStatus _status = AccountRuntimeStatus.Stopped;
    private string _statusMessage = "已停止";
    private string _loginPrompt = "";
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _lastActivityAt;
    private bool _disposed;

    public ManagedAccountDefinition Definition { get; }
    public AccountRuntimeSnapshot Snapshot => new(
        Definition.Id, Definition.LocalName, MaskPhone(Definition.PhoneNumber), Definition.TelegramUserId,
        Definition.TelegramDisplayName, Definition.AutoStart, _status, _statusMessage, _loginPrompt,
        _startedAt, _lastActivityAt);

    public AccountRuntime(
        ManagedAccountDefinition definition,
        IManagedAccountCatalog catalog,
        ISettingsStore store,
        string dataDirectory)
    {
        Definition = definition;
        _catalog = catalog;
        _store = store;
        _dataDirectory = dataDirectory;
    }

    public async Task<AccountRuntimeSnapshot> StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_status is AccountRuntimeStatus.Online or AccountRuntimeStatus.Starting or AccountRuntimeStatus.AwaitingLoginInput)
                return Snapshot;
            DisposeServices();
            _status = AccountRuntimeStatus.Starting;
            _statusMessage = "正在连接 Telegram";
            _loginPrompt = "";
            _startedAt = DateTimeOffset.Now;

            _logger = new Log4NetAppLogger(Path.Combine(_dataDirectory, "logs", Definition.Id.ToString("N")));
            _logger.EntryWritten += OnLogEntry;
            var settings = BuildSettings();
            _runtimeSettings = settings;
            _telegram = new TelegramService(_store, _logger);
            _telegram.MessageReceived += OnMessageReceived;
            _telegram.MessageDeleted += OnMessageDeleted;
            _telegram.ConnectionStateChanged += OnConnectionStateChanged;
            _scheduler = new SchedulerService(_telegram, _store, settings, _logger);
            _intervalChatAutomation = new IntervalChatAutomationService(_telegram, _store, _logger);
            _exceptionMonitor = new ExceptionMonitorService(_store, _logger, _telegram, settings);
            _mentionMonitor = new MentionMonitorService(_store, _telegram, _logger);

            var prompt = await _telegram.BeginLoginAsync(settings);
            if (prompt is null) await CompleteLoginAsync();
            else SetLoginPrompt(prompt);
            return Snapshot;
        }
        catch (Exception ex)
        {
            _status = AccountRuntimeStatus.Faulted;
            _statusMessage = SafeMessage(ex);
            _logger?.Error("AccountRuntime", "账户启动失败", ex);
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    public async Task<AccountRuntimeSnapshot> ContinueLoginAsync(string value, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (_telegram is null || _status != AccountRuntimeStatus.AwaitingLoginInput)
                throw new InvalidOperationException("当前账户没有等待登录输入");
            var prompt = await _telegram.ContinueLoginAsync(value ?? "");
            if (prompt is null) await CompleteLoginAsync(); else SetLoginPrompt(prompt);
            return Snapshot;
        }
        catch (Exception ex)
        {
            _statusMessage = SafeMessage(ex);
            _logger?.Warning("AccountRuntime", "登录输入处理失败", ex);
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    private AppSettings BuildSettings()
    {
        var settings = _store.Load();
        settings.ApiId = Definition.ApiId;
        settings.ApiHash = Definition.ApiHash;
        settings.PhoneNumber = Definition.PhoneNumber;
        settings.Proxy = Definition.Proxy;
        return settings;
    }

    private async Task CompleteLoginAsync()
    {
        if (_telegram is null || _scheduler is null || _exceptionMonitor is null || _mentionMonitor is null) return;
        Definition.TelegramUserId = _telegram.CurrentUserId;
        Definition.TelegramDisplayName = _telegram.CurrentUser;
        _catalog.Save(Definition);
        var settings = _store.Load();
        _profile = settings.Accounts.GetValueOrDefault(Definition.TelegramUserId) ?? new AccountProfile
        {
            UserId = Definition.TelegramUserId,
            LocalName = Definition.LocalName,
            DisplayName = Definition.TelegramDisplayName,
            PhoneNumber = Definition.PhoneNumber,
            AutoStart = Definition.AutoStart
        };
        _profile.LocalName = Definition.LocalName;
        _profile.DisplayName = Definition.TelegramDisplayName;
        _profile.PhoneNumber = Definition.PhoneNumber;
        _profile.AutoStart = Definition.AutoStart;
        _store.SaveAccount(_profile);
        try { await _telegram.LoadDialogsAsync(); }
        catch (Exception ex)
        {
            _logger?.Warning("AccountRuntime", "登录成功，但预加载会话失败；稍后可从页面重试", ex);
        }
        _telegram.ConfigureAutomationRules(_profile.AutomationRules);
        await _scheduler.ActivateAccountAsync(_profile);
        await _intervalChatAutomation!.ActivateAccountAsync(_profile);
        await _scheduler.RunDueTasksAsync();
        _exceptionMonitor.ActivateAccount(_profile.UserId, _profile.ExceptionAlerts);
        _mentionMonitor.ActivateAccount(_profile.UserId, _profile.MentionAlerts);
        _status = AccountRuntimeStatus.Online;
        _statusMessage = "在线";
        _loginPrompt = "";
        _lastActivityAt = DateTimeOffset.Now;
    }

    private void SetLoginPrompt(string prompt)
    {
        _status = AccountRuntimeStatus.AwaitingLoginInput;
        _loginPrompt = prompt;
        _statusMessage = prompt switch
        {
            "verification_code" => "请输入 Telegram 验证码",
            "password" => "请输入二次验证密码",
            "email" => "请输入验证邮箱",
            "email_verification_code" => "请输入邮箱验证码",
            "first_name" => "请输入名字",
            "last_name" => "请输入姓氏",
            _ => $"登录需要输入：{prompt}"
        };
    }

    private void OnConnectionStateChanged(TelegramConnectionState state)
    {
        _statusMessage = state.Message;
        _status = state.Status switch
        {
            TelegramConnectionStatus.Connected => AccountRuntimeStatus.Online,
            TelegramConnectionStatus.Recovering => AccountRuntimeStatus.Recovering,
            TelegramConnectionStatus.Disconnected => AccountRuntimeStatus.Faulted,
            _ => AccountRuntimeStatus.Starting
        };
    }

    private void OnMessageReceived(ChatLine line)
    {
        lock (_messagesSync)
        {
            UpsertMessage(line);
        }
        _lastActivityAt = DateTimeOffset.Now;
    }

    private void OnMessageDeleted(MessageDeletion deletion)
    {
        lock (_messagesSync)
        {
            foreach (var deleted in deletion.Messages)
            {
                var index = _messages.FindIndex(x => SameMessage(x, deletion.ChatKind, deletion.ChatId, deleted.MessageId));
                if (index < 0) continue; // Only show withdrawals whose original text is cached.
                _messages[index] = _messages[index] with
                {
                    Text = string.IsNullOrWhiteSpace(deleted.Text) ? _messages[index].Text : deleted.Text,
                    IsDeleted = true
                };
            }
        }
        _lastActivityAt = DateTimeOffset.Now;
    }

    private void UpsertMessage(ChatLine line)
    {
        var index = line.MessageId > 0
            ? _messages.FindIndex(x => SameMessage(x, line.ChatKind, line.ChatId, line.MessageId))
            : -1;
        if (index >= 0) _messages[index] = line;
        else _messages.Add(line);
        while (_messages.Count > 500) _messages.RemoveAt(0);
    }

    private static bool SameMessage(ChatLine line, string kind, long chatId, int messageId) =>
        line.MessageId == messageId && line.ChatId == chatId &&
        string.Equals(line.ChatKind, kind, StringComparison.OrdinalIgnoreCase);

    private void OnLogEntry(AppLogEntry entry)
    {
        lock (_logsSync)
        {
            _logs.Enqueue(entry);
            while (_logs.Count > 1000) _logs.Dequeue();
        }
    }

    public IReadOnlyList<ChatLine> GetRecentMessages(int limit)
    {
        lock (_messagesSync) return _messages.TakeLast(Math.Clamp(limit, 1, 500)).ToArray();
    }

    public IReadOnlyList<AppLogEntry> GetRuntimeLogs(AppLogLevel? level, string keyword, int limit)
    {
        lock (_logsSync)
        {
            IEnumerable<AppLogEntry> query = _logs;
            if (level.HasValue) query = query.Where(x => x.Level == level.Value);
            if (!string.IsNullOrWhiteSpace(keyword))
                query = query.Where(x =>
                    x.Category.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    x.Message.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    (x.Exception?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false));
            return query.TakeLast(Math.Clamp(limit, 1, 1000)).Reverse().ToArray();
        }
    }

    public Task<IReadOnlyList<ExceptionRecord>> QueryExceptionsAsync(ExceptionQuery query)
    {
        if (_exceptionMonitor is null) throw new InvalidOperationException("账户异常监控服务尚未启动");
        return _exceptionMonitor.QueryAsync(query with { Limit = Math.Clamp(query.Limit, 1, 1000) });
    }

    public Task RetryExceptionNotificationsAsync(IEnumerable<long> recordIds)
    {
        EnsureOnline();
        return _exceptionMonitor?.RetryNotificationsAsync(recordIds) ??
               throw new InvalidOperationException("账户异常监控服务尚未启动");
    }

    public Task<IReadOnlyList<MentionRecord>> QueryMentionsAsync(MentionQuery query)
    {
        if (_mentionMonitor is null) throw new InvalidOperationException("账户 @消息监控服务尚未启动");
        return _mentionMonitor.QueryAsync(query with { Limit = Math.Clamp(query.Limit, 1, 1000) });
    }

    public Task<IReadOnlyList<OutgoingMessageRecord>> QueryOutboxAsync(int limit)
    {
        if (_telegram is null) throw new InvalidOperationException("账户 Telegram 服务尚未启动");
        return _telegram.QueryOutboxAsync(Math.Clamp(limit, 1, 1000));
    }

    public Task RetryOutboxAsync(long recordId)
    {
        EnsureOnline();
        return _telegram!.RetryOutboxAsync(recordId);
    }

    public void UpdateEmailSettings(EmailSettings email)
    {
        if (_runtimeSettings is null) return;
        _runtimeSettings.Email.SmtpHost = email.SmtpHost;
        _runtimeSettings.Email.SmtpPort = email.SmtpPort;
        _runtimeSettings.Email.EnableSsl = email.EnableSsl;
        _runtimeSettings.Email.UserName = email.UserName;
        _runtimeSettings.Email.Password = email.Password;
        _runtimeSettings.Email.FromAddress = email.FromAddress;
    }

    public Task<IReadOnlyList<DialogItem>> LoadDialogsAsync()
    {
        EnsureOnline();
        return LoadDialogsCoreAsync();
    }

    private async Task<IReadOnlyList<DialogItem>> LoadDialogsCoreAsync() => await _telegram!.LoadDialogsAsync();

    public async Task<IReadOnlyList<ChatLine>> LoadHistoryAsync(DialogItem dialog, int limit)
    {
        EnsureOnline();
        var history = await _telegram!.LoadHistoryAsync(dialog, Math.Clamp(limit, 1, 300));
        lock (_messagesSync)
            foreach (var line in history) UpsertMessage(line);
        return history;
    }

    public Task SendAsync(SendChatMessageRequest request)
    {
        EnsureOnline();
        if (string.IsNullOrWhiteSpace(request.Message)) throw new ArgumentException("消息不能为空");
        return _telegram!.SendAsync(
            new DialogItem(request.DialogName, request.DialogId, request.DialogKind, request.DialogKind != "User"),
            request.Message.Trim());
    }

    public Task SendReplyAsync(int messageId, SendChatMessageRequest request)
    {
        EnsureOnline();
        ValidateMessageOperation(messageId, request.Message);
        return _telegram!.SendReplyAsync(ToDialog(request), messageId, request.Message.Trim());
    }

    public Task EditMessageAsync(int messageId, SendChatMessageRequest request)
    {
        EnsureOnline();
        ValidateMessageOperation(messageId, request.Message);
        return _telegram!.EditMessageAsync(ToDialog(request), messageId, request.Message.Trim());
    }

    public Task DeleteMessageAsync(int messageId, DialogItem dialog)
    {
        EnsureOnline();
        if (messageId <= 0) throw new ArgumentException("消息 ID 无效");
        return _telegram!.DeleteMessagesAsync(dialog, [messageId], revoke: true);
    }

    public Task<IReadOnlyList<string>> LoadAvailableReactionsAsync(DialogItem dialog)
    {
        EnsureOnline();
        return _telegram!.LoadAvailableReactionsAsync(dialog);
    }

    public Task SendReactionAsync(int messageId, DialogItem dialog, string emoji)
    {
        EnsureOnline();
        if (messageId <= 0) throw new ArgumentException("消息 ID 无效");
        if (string.IsNullOrWhiteSpace(emoji)) throw new ArgumentException("请选择回应表情");
        return _telegram!.SendReactionAsync(dialog, messageId, emoji.Trim());
    }

    private static DialogItem ToDialog(SendChatMessageRequest request) =>
        new(request.DialogName, request.DialogId, request.DialogKind, request.DialogKind != "User");

    private static void ValidateMessageOperation(int messageId, string message)
    {
        if (messageId <= 0) throw new ArgumentException("消息 ID 无效");
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("消息不能为空");
    }

    public IReadOnlyList<ScheduledMessage> GetSchedules() => _profile?.Schedules.ToArray() ?? [];

    public async Task UpsertScheduleAsync(ScheduledMessage schedule)
    {
        EnsureOnline();
        if (_profile is null || _scheduler is null) throw new InvalidOperationException("账户任务尚未加载");
        if (schedule.ChatId == 0 || string.IsNullOrWhiteSpace(schedule.Message))
            throw new ArgumentException("群聊和发送内容不能为空");
        var index = _profile.Schedules.FindIndex(x => x.Id == schedule.Id);
        if (index < 0) _profile.Schedules.Add(schedule); else _profile.Schedules[index] = schedule;
        _store.SaveAccount(_profile);
        await _scheduler.UpsertAsync(schedule);
    }

    public async Task DeleteScheduleAsync(Guid scheduleId)
    {
        EnsureOnline();
        if (_profile is null || _scheduler is null) return;
        _profile.Schedules.RemoveAll(x => x.Id == scheduleId);
        _store.SaveAccount(_profile);
        await _scheduler.DeleteAsync(scheduleId);
    }

    public Task ExecuteScheduleAsync(Guid scheduleId)
    {
        EnsureOnline();
        return _scheduler!.ExecuteNowAsync(scheduleId);
    }

    public IReadOnlyList<IntervalChatRule> GetIntervalChatRules() =>
        _profile?.IntervalChatRules.ToArray() ?? [];

    public async Task UpsertIntervalChatRuleAsync(IntervalChatRule rule)
    {
        EnsureOnline();
        if (_profile is null || _intervalChatAutomation is null)
            throw new InvalidOperationException("账户间隔分析服务尚未加载");
        var index = _profile.IntervalChatRules.FindIndex(x => x.Id == rule.Id);
        if (index < 0) _profile.IntervalChatRules.Add(rule); else _profile.IntervalChatRules[index] = rule;
        _store.SaveAccount(_profile);
        await _intervalChatAutomation.UpsertAsync(rule);
    }

    public async Task DeleteIntervalChatRuleAsync(Guid ruleId)
    {
        EnsureOnline();
        if (_profile is null || _intervalChatAutomation is null) return;
        _profile.IntervalChatRules.RemoveAll(x => x.Id == ruleId);
        _store.SaveAccount(_profile);
        await _intervalChatAutomation.DeleteAsync(ruleId);
    }

    public Task ExecuteIntervalChatRuleAsync(Guid ruleId)
    {
        EnsureOnline();
        return _intervalChatAutomation!.ExecuteNowAsync(ruleId);
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            DisposeServices();
            _status = AccountRuntimeStatus.Stopped;
            _statusMessage = "已停止";
            _loginPrompt = "";
            _startedAt = null;
        }
        finally { _lifecycle.Release(); }
    }

    private void DisposeServices()
    {
        if (_telegram is not null) _telegram.MessageReceived -= OnMessageReceived;
        if (_telegram is not null) _telegram.MessageDeleted -= OnMessageDeleted;
        if (_telegram is not null) _telegram.ConnectionStateChanged -= OnConnectionStateChanged;
        _mentionMonitor?.Dispose();
        _exceptionMonitor?.Dispose();
        _intervalChatAutomation?.Dispose();
        _scheduler?.Dispose();
        _telegram?.Dispose();
        if (_logger is not null) _logger.EntryWritten -= OnLogEntry;
        _logger?.Dispose();
        _mentionMonitor = null;
        _exceptionMonitor = null;
        _intervalChatAutomation = null;
        _scheduler = null;
        _telegram = null;
        _logger = null;
        _profile = null;
        _runtimeSettings = null;
    }

    private void EnsureOnline()
    {
        if (_status != AccountRuntimeStatus.Online || _telegram?.IsLoggedIn != true)
            throw new InvalidOperationException("账户当前不在线");
    }

    private static string MaskPhone(string phone)
    {
        if (phone.Length < 7) return "***";
        return phone[..Math.Min(3, phone.Length)] + new string('*', Math.Max(3, phone.Length - 7)) + phone[^4..];
    }

    private static string SafeMessage(Exception ex) => ex is AggregateException aggregate
        ? aggregate.GetBaseException().Message
        : ex.Message;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
        _lifecycle.Dispose();
    }
}
