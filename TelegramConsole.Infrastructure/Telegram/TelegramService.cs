using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TelegramConsole.Core;
using TL;
using TLMessage = TL.Message;

namespace TelegramConsole.Infrastructure;

public sealed class TelegramService : ITelegramService
{
    private WTelegram.Client? _client;
    private WTelegram.UpdateManager? _manager;
    private readonly ISettingsStore _store;
    private readonly IAppLogger _logger;
    private readonly OutgoingMessageStore _outbox;
    private readonly MessageIndexStore _messageIndex;
    private readonly Dictionary<string, InputPeer> _peers = [];
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _recoverySync = new();
    private TelegramConnectionStatus? _lastConnectionStatus;
    private AppSettings? _connectionSettings;
    private Task? _recoveryTask;
    private bool _suppressDisconnectLogging;
    private bool _disposed;
    private IReadOnlyList<AutomationRule> _automationRules = [];

    public bool IsLoggedIn => _client?.User is not null && !_client.Disconnected;
    public string OutboxDatabasePath => _outbox.DatabasePath;
    public long CurrentUserId => _client?.User?.id ?? 0;
    public string CurrentUser => _client?.User?.ToString() ?? "未登录";
    public event Action<ChatLine>? MessageReceived;
    public event Action<string>? Log;
    public event Action<TelegramConnectionState>? ConnectionStateChanged;
    public event Action? OutboxChanged;
    public event Action<string>? AutomationActivity;

    public TelegramService(ISettingsStore store, IAppLogger logger)
    {
        _store = store;
        _logger = logger;
        _outbox = new OutgoingMessageStore(store);
        _messageIndex = new MessageIndexStore(store);
        WTelegram.Helpers.Log = (level, message) =>
        {
            _logger.Write(level >= 4 ? AppLogLevel.Warning : AppLogLevel.Debug, "WTelegram", message);
            if (level >= 3 && !IsConnectionFailureLog(message)) Log?.Invoke(message);
            HandleConnectionLog(message);
        };
    }

    public async Task<string?> BeginLoginAsync(AppSettings settings)
    {
        await _connectionLock.WaitAsync();
        try
        {
            _connectionSettings = settings;
            return await CreateClientAndLoginAsync(
                settings,
                "开始连接并登录 Telegram",
                "正在连接 Telegram...",
                clearPeers: true);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<string?> ContinueLoginAsync(string value)
    {
        if (_client is null) throw new InvalidOperationException("请先点击登录");
        var prompt = await _client.Login(value);
        _logger.Info("Telegram", prompt is null ? "Telegram 登录成功" : $"Telegram 登录继续等待输入：{prompt}");
        if (prompt is null) ReportConnection(TelegramConnectionStatus.Connected, $"Telegram 已连接：{CurrentUser}");
        return prompt;
    }

    private async Task<string?> CreateClientAndLoginAsync(
        AppSettings settings,
        string logMessage,
        string statusMessage,
        bool clearPeers)
    {
        _logger.Info("Telegram", logMessage);
        ReportConnection(TelegramConnectionStatus.Connecting, statusMessage);
        DisposeClientForReconnect(clearPeers);
        _client = new WTelegram.Client(settings.ApiId, settings.ApiHash, _store.GetSessionPath(settings.PhoneNumber));
        ConfigureClientConnection(_client);
        ApplyProxy(_client, settings.Proxy);
        _client.OnOther += OnOther;
        _manager = _client.WithUpdateManager(OnUpdate);
        var prompt = await _client.Login(settings.PhoneNumber);
        _logger.Info("Telegram", prompt is null ? "Telegram 登录成功" : $"Telegram 登录等待输入：{prompt}");
        if (prompt is null) ReportConnection(TelegramConnectionStatus.Connected, $"Telegram 已连接：{CurrentUser}");
        return prompt;
    }

    private void DisposeClientForReconnect(bool clearPeers)
    {
        _suppressDisconnectLogging = true;
        try
        {
            if (_client is not null) _client.OnOther -= OnOther;
            _client?.Dispose();
        }
        finally
        {
            _suppressDisconnectLogging = false;
        }
        _manager = null;
        if (clearPeers) _peers.Clear();
    }

    private Task OnOther(IObject notification)
    {
        if (notification is ReactorError reactorError && !_suppressDisconnectLogging)
            QueueConnectionRecovery(reactorError.Exception);
        return Task.CompletedTask;
    }

    private void HandleConnectionLog(string message)
    {
        if (_suppressDisconnectLogging) return;
        if (message.Contains("Connected to", StringComparison.OrdinalIgnoreCase) && _client?.User is not null)
        {
            ReportConnection(TelegramConnectionStatus.Connected, $"Telegram 已重新连接：{CurrentUser}");
            return;
        }

        if (_client?.User is null || message.Contains("Alt DC disconnected", StringComparison.OrdinalIgnoreCase)) return;
        if (IsConnectionFailureLog(message)) QueueConnectionRecovery(new IOException(message));
    }

    private void QueueConnectionRecovery(Exception exception)
    {
        if (_disposed || _suppressDisconnectLogging || _client?.User is null || _connectionSettings is null) return;
        lock (_recoverySync)
        {
            if (_recoveryTask is { IsCompleted: false }) return;
            var failedClient = _client;
            var settings = _connectionSettings;
            _recoveryTask = Task.Run(() => RecoverConnectionAsync(failedClient, settings, exception));
        }
    }

    private async Task RecoverConnectionAsync(
        WTelegram.Client failedClient,
        AppSettings settings,
        Exception reactorException)
    {
        ReportConnection(
            TelegramConnectionStatus.Recovering,
            "Telegram 连接异常，正在等待客户端内部重连...",
            reactorException);

        try
        {
            await failedClient.Invoke(new TL.Methods.Ping { ping_id = Random.Shared.NextInt64() });
            if (!_disposed && ReferenceEquals(_client, failedClient) && !failedClient.Disconnected)
            {
                ReportConnection(TelegramConnectionStatus.Connected, $"Telegram 已重新连接：{CurrentUser}");
                return;
            }
        }
        catch (Exception probeException)
        {
            if (_disposed || !ReferenceEquals(_client, failedClient)) return;
            if (!failedClient.Disconnected)
            {
                _logger.Warning(
                    "Telegram.Connection",
                    $"Telegram API 探测失败，但客户端未处于断开状态，暂不判定为真正断线：{probeException.Message}");
                return;
            }

            _logger.Error(
                "Telegram.Connection",
                "Telegram API 探测失败且客户端仍处于断开状态，确认主连接不可用",
                probeException);
        }

        if (_disposed || !ReferenceEquals(_client, failedClient) || !failedClient.Disconnected) return;

        await _connectionLock.WaitAsync();
        try
        {
            if (_disposed || !ReferenceEquals(_client, failedClient)) return;
            var prompt = await CreateClientAndLoginAsync(
                settings,
                "WTelegram 内部恢复失败，正在使用现有会话重建连接",
                "Telegram 内部恢复失败，正在重建连接...",
                clearPeers: false);
            if (prompt is not null)
                ReportConnection(
                    TelegramConnectionStatus.Disconnected,
                    "自动重连需要重新验证，请完成 Telegram 登录验证。");
        }
        catch (Exception reconnectException)
        {
            if (!_disposed)
                ReportConnection(
                    TelegramConnectionStatus.Disconnected,
                    "Telegram 自动重连失败，请检查网络或代理后重新登录。",
                    reconnectException);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private static bool IsConnectionFailureLog(string message) =>
        message.Contains("Connection shut down", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Could not read payload length", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("connection lost", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("connection closed", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("disconnected", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("exception occured in the reactor", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("exception occurred in the reactor", StringComparison.OrdinalIgnoreCase);

    private void ReportConnection(TelegramConnectionStatus status, string message, Exception? exception = null)
    {
        if (_lastConnectionStatus == status) return;
        _lastConnectionStatus = status;
        if (status == TelegramConnectionStatus.Recovering)
            _logger.Error(
                "Telegram.Connection",
                "Telegram 主连接发生异常，正在通过 API 探测等待内部恢复",
                exception ?? new IOException(message));
        else if (status == TelegramConnectionStatus.Disconnected)
            _logger.Error(
                "Telegram.Connection",
                "Telegram 主连接确认不可用，需要人工处理",
                exception ?? new IOException(message));
        else if (status == TelegramConnectionStatus.Connected)
            _logger.Info("Telegram.Connection", "Telegram 主连接已建立或恢复");
        ConnectionStateChanged?.Invoke(new(status, message));
    }

    public async Task<List<DialogItem>> LoadDialogsAsync()
    {
        EnsureLogin();
        var dialogs = await _client!.Messages_GetAllDialogs();
        dialogs.CollectUsersChats(_manager!.Users, _manager.Chats);
        _peers.Clear();
        var result = new List<DialogItem>();
        foreach (var dialog in dialogs.dialogs)
        {
            var info = dialogs.UserOrChat(dialog);
            switch (info)
            {
                case User user when user.IsActive:
                    var userName = string.Join(' ', new[] { user.first_name, user.last_name }.Where(x => !string.IsNullOrWhiteSpace(x)));
                    if (string.IsNullOrWhiteSpace(userName)) userName = user.username ?? user.id.ToString();
                    var userItem = new DialogItem(userName, user.id, "User", false);
                    _peers[PeerKey(userItem.Id, userItem.Kind)] = user.ToInputPeer();
                    result.Add(userItem);
                    break;
                case ChatBase chat when chat.IsActive:
                    var kind = chat is Channel ? "Channel" : "Chat";
                    var chatItem = new DialogItem(chat.Title ?? chat.ID.ToString(), chat.ID, kind, true);
                    _peers[PeerKey(chatItem.Id, chatItem.Kind)] = chat.ToInputPeer();
                    result.Add(chatItem);
                    break;
            }
        }
        var sorted = result.OrderByDescending(x => x.IsGroup).ThenBy(x => x.Name).ToList();
        _logger.Info("Telegram", $"已加载 {sorted.Count} 个会话，其中 {sorted.Count(x => x.IsGroup)} 个群聊或频道");
        return sorted;
    }

    public async Task<List<ChatLine>> LoadHistoryAsync(DialogItem dialog, int limit = 50)
    {
        EnsureLogin();
        _logger.Write(AppLogLevel.Debug, "Telegram", $"加载会话历史：{dialog.Kind}/{dialog.Id}，数量上限 {limit}");
        var history = await _client!.Messages_GetHistory(ResolvePeer(dialog.Id, dialog.Kind), limit: limit);
        history.CollectUsersChats(_manager!.Users, _manager.Chats);
        var lines = history.Messages
            .OfType<TLMessage>()
            .OrderBy(x => x.date)
            .Select(m => new ChatLine(
                m.date.ToLocalTime(), dialog.Name, NameOf(m.from_id),
                string.IsNullOrWhiteSpace(m.message) ? "[媒体消息]" : m.message,
                dialog.IsGroup, dialog.Id,
                m.flags.HasFlag(TLMessage.Flags.out_), IsMentioned(m), m.id, dialog.Kind,
                TopicIdOf(m)))
            .ToList();
        await IndexMessagesAsync(lines);
        return lines;
    }

    public void ConfigureAutomationRules(IReadOnlyList<AutomationRule> rules) =>
        _automationRules = rules.Where(x => x.Enabled).ToArray();

    public async Task<IReadOnlyList<MessageSearchResult>> SearchMessagesAsync(
        string query, DialogItem? dialog = null, int limit = 100)
    {
        EnsureLogin();
        if (string.IsNullOrWhiteSpace(query)) return [];
        limit = Math.Clamp(limit, 1, 500);
        var local = await _messageIndex.SearchAsync(CurrentUserId, query, dialog, limit);
        var remote = dialog is null
            ? await _client!.Messages_SearchGlobal(q: query, filter: null!, limit: limit)
            : await _client!.Messages_Search(
                peer: ResolvePeer(dialog.Id, dialog.Kind), q: query, filter: null!, limit: limit);
        remote.CollectUsersChats(_manager!.Users, _manager.Chats);
        var mapped = remote.Messages.OfType<TLMessage>()
            .Select(ToSearchResult)
            .Where(x => dialog is null || x.ChatId == dialog.Id)
            .ToList();
        await _messageIndex.IndexAsync(CurrentUserId, mapped);
        return mapped.Concat(local)
            .GroupBy(x => (x.ChatId, x.MessageId))
            .Select(x => x.First())
            .OrderByDescending(x => x.Time)
            .Take(limit)
            .ToArray();
    }

    public async Task<IReadOnlyList<ForumTopicItem>> LoadForumTopicsAsync(DialogItem dialog)
    {
        EnsureLogin();
        if (dialog.Kind != "Channel") return [];
        var topics = await _client!.Channels_GetAllForumTopics(ResolvePeer(dialog.Id, dialog.Kind), "");
        return topics.topics.OfType<ForumTopic>()
            .Select(x => new ForumTopicItem(x.id, x.title, x.unread_count))
            .OrderBy(x => x.Title)
            .ToArray();
    }

    public async Task<ServerScheduledMessage> ScheduleServerMessageAsync(
        DialogItem dialog, string text, DateTime sendAt)
    {
        EnsureLogin();
        if (sendAt <= DateTime.Now.AddSeconds(10))
            throw new InvalidOperationException("服务器定时发送时间至少应晚于当前时间 10 秒");
        await _client!.Messages_SendMessage(
            ResolvePeer(dialog.Id, dialog.Kind), text, Random.Shared.NextInt64(),
            schedule_date: sendAt.ToUniversalTime());
        var items = await LoadServerScheduledMessagesAsync(dialog);
        var result = items.OrderBy(x => Math.Abs((x.SendAt - sendAt).TotalSeconds))
            .FirstOrDefault(x => x.Text == text)
            ?? throw new InvalidOperationException("Telegram 已接受任务，但未能读取计划消息编号");
        _logger.Info("Telegram.Schedule", $"已创建服务器定时消息：{dialog.Name} / {result.MessageId} / {sendAt:O}");
        return result;
    }

    public async Task<IReadOnlyList<ServerScheduledMessage>> LoadServerScheduledMessagesAsync(DialogItem dialog)
    {
        EnsureLogin();
        var history = await _client!.Messages_GetScheduledHistory(ResolvePeer(dialog.Id, dialog.Kind), 0);
        return history.Messages.OfType<TLMessage>()
            .OrderBy(x => x.date)
            .Select(x => new ServerScheduledMessage(
                dialog.Id, dialog.Kind, dialog.Name, x.id, x.date.ToLocalTime(),
                string.IsNullOrWhiteSpace(x.message) ? "[媒体消息]" : x.message))
            .ToArray();
    }

    public async Task DeleteServerScheduledMessagesAsync(
        DialogItem dialog, IReadOnlyCollection<int> messageIds)
    {
        EnsureLogin();
        if (messageIds.Count == 0) return;
        await _client!.Messages_DeleteScheduledMessages(ResolvePeer(dialog.Id, dialog.Kind), messageIds.ToArray());
        _logger.Info("Telegram.Schedule", $"已删除 {messageIds.Count} 条服务器定时消息：{dialog.Name}");
    }

    public async Task SendReplyAsync(DialogItem dialog, int replyToMessageId, string text, string quote = "")
    {
        EnsureLogin();
        var reply = new InputReplyToMessage { reply_to_msg_id = replyToMessageId };
        if (!string.IsNullOrWhiteSpace(quote)) reply.quote_text = quote;
        await _client!.Messages_SendMessage(
            ResolvePeer(dialog.Id, dialog.Kind), text, Random.Shared.NextInt64(), reply_to: reply);
    }

    public async Task EditMessageAsync(DialogItem dialog, int messageId, string text)
    {
        EnsureLogin();
        await _client!.Messages_EditMessage(ResolvePeer(dialog.Id, dialog.Kind), messageId, message: text);
    }

    public async Task DeleteMessagesAsync(
        DialogItem dialog, IReadOnlyCollection<int> messageIds, bool revoke = true)
    {
        EnsureLogin();
        if (messageIds.Count == 0) return;
        if (dialog.Kind == "Channel" && _manager!.Chats.TryGetValue(dialog.Id, out var chat) && chat is Channel channel)
            await _client!.Channels_DeleteMessages(new InputChannel(channel.id, channel.access_hash), messageIds.ToArray());
        else
            await _client!.Messages_DeleteMessages(messageIds.ToArray(), revoke);
    }

    public async Task ForwardMessagesAsync(
        DialogItem source, IReadOnlyCollection<int> messageIds, DialogItem target)
    {
        EnsureLogin();
        if (messageIds.Count == 0) return;
        await _client!.ForwardMessagesAsync(
            ResolvePeer(source.Id, source.Kind), messageIds.ToArray(), ResolvePeer(target.Id, target.Kind));
    }

    public string GetMessageLink(DialogItem dialog, int messageId)
    {
        if (dialog.Kind != "Channel") return "";
        if (_manager?.Chats.TryGetValue(dialog.Id, out var chat) == true &&
            chat is Channel channel && !string.IsNullOrWhiteSpace(channel.MainUsername))
            return $"https://t.me/{channel.MainUsername}/{messageId}";
        return $"https://t.me/c/{dialog.Id}/{messageId}";
    }

    public async Task SaveCloudDraftAsync(DialogItem dialog, string text, int? replyToMessageId = null)
    {
        EnsureLogin();
        var reply = replyToMessageId is int id ? new InputReplyToMessage { reply_to_msg_id = id } : null;
        await _client!.Messages_SaveDraft(ResolvePeer(dialog.Id, dialog.Kind), text, reply_to: reply);
    }

    public async Task<string> LoadCloudDraftAsync(DialogItem dialog)
    {
        EnsureLogin();
        var result = await _client!.Messages_GetPeerDialogs([
            new InputDialogPeer { peer = ResolvePeer(dialog.Id, dialog.Kind) }
        ]);
        return result.dialogs.OfType<Dialog>().FirstOrDefault()?.draft is DraftMessage draft
            ? draft.message
            : "";
    }

    public async Task<IReadOnlyList<DialogFolderItem>> LoadDialogFoldersAsync()
    {
        EnsureLogin();
        var filters = await _client!.Messages_GetDialogFilters();
        return filters.filters
            .Select(x => new DialogFolderItem(x.ID, x.Title?.text ?? $"#{x.ID}", x.IncludePeers?.Length ?? 0))
            .ToArray();
    }

    public async Task CreateDialogFolderAsync(string title, IReadOnlyCollection<DialogItem> dialogs)
    {
        EnsureLogin();
        if (string.IsNullOrWhiteSpace(title)) throw new InvalidOperationException("文件夹名称不能为空");
        if (dialogs.Count == 0) throw new InvalidOperationException("请至少选择一个会话");
        var existing = await _client!.Messages_GetDialogFilters();
        var id = Enumerable.Range(2, 8).FirstOrDefault(x => existing.filters.All(f => f.ID != x));
        if (id == 0) throw new InvalidOperationException("Telegram 自定义文件夹数量已达到当前账号限制");
        var peers = dialogs.Select(x => ResolvePeer(x.Id, x.Kind)).ToArray();
        var filter = new DialogFilter
        {
            id = id,
            title = new TextWithEntities { text = title.Trim(), entities = [] },
            pinned_peers = [],
            include_peers = peers,
            exclude_peers = []
        };
        await _client.Messages_UpdateDialogFilter(id, filter);
    }

    public async Task SendAsync(DialogItem dialog, string text)
    {
        var key = BuildManualIdempotencyKey(dialog.Id, dialog.Kind, text);
        await SendReliableAsync(
            dialog.Id, dialog.Kind, dialog.Name, "Manual", text, key,
            allowFailedRetry: false, allowUnknownRetry: false);
    }

    public async Task SendScheduledAsync(ScheduledMessage schedule)
    {
        var key = $"schedule:{schedule.Id:N}:{DateOnly.FromDateTime(DateTime.Now):yyyyMMdd}";
        await SendReliableAsync(
            schedule.ChatId, schedule.ChatKind, schedule.ChatTitle, "Schedule", schedule.Message, key,
            allowFailedRetry: true, allowUnknownRetry: false);
    }

    public async Task SendConfirmationAsync(ScheduledMessage schedule, string text)
    {
        EnsureLogin();
        if (schedule.ConfirmationPeerId is not long id) return;
        var key = $"confirmation:{schedule.Id:N}:{DateOnly.FromDateTime(DateTime.Now):yyyyMMdd}:{id}";
        await SendReliableAsync(
            id, schedule.ConfirmationPeerKind, schedule.ConfirmationPeerTitle,
            "Confirmation", text, key, allowFailedRetry: true, allowUnknownRetry: false);
    }

    public async Task<IReadOnlyList<OutgoingMessageRecord>> QueryOutboxAsync(int limit = 200) =>
        CurrentUserId == 0 ? [] : await _outbox.QueryAsync(CurrentUserId, limit);

    public async Task RetryOutboxAsync(long recordId)
    {
        EnsureLogin();
        var record = await _outbox.GetAsync(CurrentUserId, recordId)
                     ?? throw new InvalidOperationException("找不到发件箱记录");
        if (record.Status == OutgoingMessageStatus.Sent)
            throw new InvalidOperationException("消息已经发送成功，无需重试");
        await SendReliableAsync(
            record.TargetId, record.TargetKind, record.TargetTitle, record.Purpose,
            record.Message, record.IdempotencyKey,
            allowFailedRetry: true, allowUnknownRetry: true);
    }

    private async Task SendReliableAsync(
        long targetId,
        string targetKind,
        string targetTitle,
        string purpose,
        string message,
        string idempotencyKey,
        bool allowFailedRetry,
        bool allowUnknownRetry)
    {
        EnsureLogin();
        await _sendLock.WaitAsync();
        try
        {
            EnsureLogin();
            var accountId = CurrentUserId;
            var record = await _outbox.GetOrCreateAsync(
                accountId, idempotencyKey, targetId, targetKind, targetTitle,
                purpose, message);
            if (record.Status == OutgoingMessageStatus.Sent)
            {
                _logger.Warning("Telegram.Outbox", $"已阻止重复发送：记录 {record.Id}，目标 {targetKind}/{targetId}");
                return;
            }
            if (record.Status == OutgoingMessageStatus.Unknown && !allowUnknownRetry)
                throw new InvalidOperationException("上一笔相同消息的发送结果未知，请在发件箱确认后手动重试");
            if (record.Status == OutgoingMessageStatus.Failed && !allowFailedRetry)
                throw new InvalidOperationException("上一笔相同消息发送失败，请在发件箱中重试");

            var attempt = record.AttemptCount + 1;
            await _outbox.UpdateAsync(record.Id, OutgoingMessageStatus.Sending, attempt);
            OutboxChanged?.Invoke();
            try
            {
                var sent = await _client!.SendMessageAsync(ResolvePeer(targetId, targetKind), message);
                await _outbox.UpdateAsync(
                    record.Id, OutgoingMessageStatus.Sent, attempt, sent.id);
                _logger.Info(
                    "Telegram.Outbox",
                    $"消息发送成功：记录 {record.Id}，{targetKind}/{targetId}，消息 ID {sent.id}，字符数 {message.Length}");
                OutboxChanged?.Invoke();
            }
            catch (Exception ex)
            {
                var status = IsUnknownDelivery(ex)
                    ? OutgoingMessageStatus.Unknown
                    : OutgoingMessageStatus.Failed;
                await _outbox.UpdateAsync(record.Id, status, attempt, error: ex.Message);
                _logger.Error(
                    "Telegram.Outbox",
                    status == OutgoingMessageStatus.Unknown
                        ? $"消息发送结果未知：记录 {record.Id}，不会自动重发"
                        : $"消息发送明确失败：记录 {record.Id}",
                    ex);
                OutboxChanged?.Invoke();
                throw;
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static string BuildManualIdempotencyKey(long targetId, string targetKind, string message)
    {
        var bucket = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 3000;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{targetKind}:{targetId}:{message}"));
        return $"manual:{bucket}:{Convert.ToHexString(bytes)[..20]}";
    }

    private static bool IsUnknownDelivery(Exception exception)
    {
        if (exception is IOException or TimeoutException or OperationCanceledException) return true;
        var text = exception.ToString();
        return text.Contains("Connection shut down", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Could not read payload", StringComparison.OrdinalIgnoreCase)
               || text.Contains("connection lost", StringComparison.OrdinalIgnoreCase)
               || text.Contains("reactor", StringComparison.OrdinalIgnoreCase)
               || text.Contains("transport", StringComparison.OrdinalIgnoreCase);
    }

    private InputPeer ResolvePeer(long id, string kind)
    {
        if (_peers.TryGetValue(PeerKey(id, kind), out var peer)) return peer;
        if (kind == "User" && _manager!.Users.TryGetValue(id, out var user)) return user.ToInputPeer();
        if (kind == "Chat") return new InputPeerChat(id);
        if (_manager!.Chats.TryGetValue(id, out var chat)) return chat.ToInputPeer();
        throw new InvalidOperationException($"找不到会话 {id}，请刷新会话后再试");
    }

    private async Task OnUpdate(Update update)
    {
        TLMessage? message = update switch
        {
            UpdateNewMessage x => x.message as TLMessage,
            UpdateEditMessage x => x.message as TLMessage,
            _ => null
        };
        if (message is null) return;

        var peerInfo = _manager?.UserOrChat(message.peer_id);
        var isGroup = peerInfo is ChatBase;
        var chatName = peerInfo switch
        {
            ChatBase chat => chat.Title ?? chat.ID.ToString(),
            User user => DisplayName(user),
            _ => $"ID {message.peer_id.ID}"
        };
        var chatKind = peerInfo switch
        {
            Channel => "Channel",
            Chat => "Chat",
            _ => "User"
        };
        var line = new ChatLine(
            message.date.ToLocalTime(), chatName, NameOf(message.from_id),
            string.IsNullOrWhiteSpace(message.message) ? "[媒体消息]" : message.message,
            isGroup, message.peer_id.ID,
            message.flags.HasFlag(TLMessage.Flags.out_), IsMentioned(message), message.id, chatKind,
            TopicIdOf(message));
        MessageReceived?.Invoke(line);
        await IndexMessagesAsync([line]);
        if (!line.IsOutgoing) await ProcessAutomationRulesAsync(line);
    }

    private async Task IndexMessagesAsync(IEnumerable<ChatLine> lines)
    {
        if (CurrentUserId == 0) return;
        var items = lines.Where(x => x.MessageId != 0).Select(x => new MessageSearchResult(
            x.ChatId, x.ChatKind, x.Chat, x.MessageId, x.Time, x.Sender, x.Text,
            x.IsOutgoing, x.TopicId, "本地索引"));
        await _messageIndex.IndexAsync(CurrentUserId, items);
    }

    private MessageSearchResult ToSearchResult(TLMessage message)
    {
        var info = _manager?.UserOrChat(message.peer_id);
        var kind = info switch { Channel => "Channel", Chat => "Chat", _ => "User" };
        var title = info switch
        {
            ChatBase chat => chat.Title ?? chat.ID.ToString(),
            User user => DisplayName(user),
            _ => message.peer_id.ID.ToString()
        };
        return new(
            message.peer_id.ID, kind, title, message.id, message.date.ToLocalTime(),
            NameOf(message.from_id), string.IsNullOrWhiteSpace(message.message) ? "[媒体消息]" : message.message,
            message.flags.HasFlag(TLMessage.Flags.out_), TopicIdOf(message));
    }

    private static int? TopicIdOf(TLMessage message) =>
        message.reply_to is MessageReplyHeader header && header.TopicID != 0 ? header.TopicID : null;

    private async Task ProcessAutomationRulesAsync(ChatLine line)
    {
        foreach (var rule in _automationRules.Where(x => MatchesSafely(x, line)))
        {
            var text = rule.MessageTemplate
                .Replace("{规则}", rule.Name)
                .Replace("{Rule}", rule.Name)
                .Replace("{群聊}", line.Chat)
                .Replace("{Chat}", line.Chat)
                .Replace("{发送人}", line.Sender)
                .Replace("{Sender}", line.Sender)
                .Replace("{内容}", line.Text)
                .Replace("{Content}", line.Text)
                .Replace("{时间}", line.Time.ToString("yyyy-MM-dd HH:mm:ss"))
                .Replace("{Time}", line.Time.ToString("yyyy-MM-dd HH:mm:ss"));
            try
            {
                switch (rule.Action)
                {
                    case AutomationAction.Telegram when rule.TargetPeerId is long targetId:
                        await SendReliableAsync(
                            targetId, rule.TargetPeerKind, rule.TargetPeerTitle, "Automation", text,
                            $"automation:{rule.Id:N}:{line.ChatId}:{line.MessageId}",
                            allowFailedRetry: true, allowUnknownRetry: false);
                        break;
                    case AutomationAction.Email when !string.IsNullOrWhiteSpace(rule.EmailRecipient):
                        if (_connectionSettings is null) throw new InvalidOperationException("尚未加载邮件配置");
                        await EmailNotificationService.SendAsync(
                            _connectionSettings.Email, rule.EmailRecipient, $"Telegram 规则：{rule.Name}", text);
                        break;
                    default:
                        _logger.Info("Automation", text);
                        break;
                }
                AutomationActivity?.Invoke($"规则“{rule.Name}”已执行");
            }
            catch (Exception ex)
            {
                _logger.Error("Automation", $"规则“{rule.Name}”执行失败", ex);
                AutomationActivity?.Invoke($"规则“{rule.Name}”执行失败：{ex.Message}");
            }
        }
    }

    private static bool Matches(AutomationRule rule, ChatLine line)
    {
        if (rule.ChatId is long chatId && line.ChatId != chatId) return false;
        return rule.Trigger switch
        {
            AutomationTrigger.Mention => line.IsMentioned,
            AutomationTrigger.Chat => rule.ChatId is null || line.ChatId == rule.ChatId,
            AutomationTrigger.Sender => line.Sender.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase),
            AutomationTrigger.RegularExpression => Regex.IsMatch(
                line.Text, rule.Pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(500)),
            _ => line.Text.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase)
        };
    }

    private bool MatchesSafely(AutomationRule rule, ChatLine line)
    {
        try { return Matches(rule, line); }
        catch (Exception ex)
        {
            _logger.Error("Automation", $"规则“{rule.Name}”的匹配表达式无效", ex);
            AutomationActivity?.Invoke($"规则“{rule.Name}”匹配失败：{ex.Message}");
            return false;
        }
    }

    private string NameOf(Peer? peer)
    {
        if (peer is null) return CurrentUser;
        return _manager?.UserOrChat(peer) switch
        {
            User user => DisplayName(user),
            ChatBase chat => chat.Title,
            _ => $"ID {peer.ID}"
        } ?? "未知";
    }

    private static string DisplayName(User user)
    {
        var name = string.Join(' ', new[] { user.first_name, user.last_name }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return string.IsNullOrWhiteSpace(name) ? user.username ?? user.id.ToString() : name;
    }

    private bool IsMentioned(TLMessage message)
    {
        if (message.flags.HasFlag(TLMessage.Flags.mentioned)) return true;
        var username = _client?.User?.username;
        return !string.IsNullOrWhiteSpace(username)
            && message.message?.Contains("@" + username, StringComparison.OrdinalIgnoreCase) == true;
    }

    private void EnsureLogin()
    {
        if (!IsLoggedIn) throw new InvalidOperationException("请先登录 Telegram");
    }

    private static void ApplyProxy(WTelegram.Client client, ProxySettings proxy)
    {
        if (!proxy.Enabled) return;
        if (proxy.Type.Equals("MtProxy", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(proxy.MtProxyUrl))
                throw new InvalidOperationException("已启用 MTProxy，但代理链接为空");
            client.MTProxyUrl = proxy.MtProxyUrl.Trim();
            return;
        }
        if (string.IsNullOrWhiteSpace(proxy.Host) || proxy.Port is < 1 or > 65535)
            throw new InvalidOperationException("SOCKS5 代理地址或端口不正确");
        client.TcpHandler = (host, port) => Socks5ProxyConnector.ConnectAsync(proxy, host, port);
    }

    private static void ConfigureClientConnection(WTelegram.Client client)
    {
        client.PingInterval = 30;
    }

    private static string PeerKey(long id, string kind) => $"{kind}:{id}";

    public void Dispose()
    {
        _disposed = true;
        _logger.Info("Telegram", "Telegram 服务正在关闭");
        DisposeClientForReconnect(clearPeers: true);
        _sendLock.Dispose();
    }
}
