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
    private readonly Dictionary<string, InputPeer> _peers = [];
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly object _recoverySync = new();
    private TelegramConnectionStatus? _lastConnectionStatus;
    private AppSettings? _connectionSettings;
    private Task? _recoveryTask;
    private bool _suppressDisconnectLogging;
    private bool _disposed;

    public bool IsLoggedIn => _client?.User is not null && !_client.Disconnected;
    public long CurrentUserId => _client?.User?.id ?? 0;
    public string CurrentUser => _client?.User?.ToString() ?? "未登录";
    public event Action<ChatLine>? MessageReceived;
    public event Action<string>? Log;
    public event Action<TelegramConnectionState>? ConnectionStateChanged;

    public TelegramService(ISettingsStore store, IAppLogger logger)
    {
        _store = store;
        _logger = logger;
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
        return history.Messages
            .OfType<TLMessage>()
            .OrderBy(x => x.date)
            .Select(m => new ChatLine(
                m.date.ToLocalTime(), dialog.Name, NameOf(m.from_id),
                string.IsNullOrWhiteSpace(m.message) ? "[媒体消息]" : m.message,
                dialog.IsGroup, dialog.Id,
                m.flags.HasFlag(TLMessage.Flags.out_), IsMentioned(m)))
            .ToList();
    }

    public async Task SendAsync(DialogItem dialog, string text)
    {
        EnsureLogin();
        await _client!.SendMessageAsync(ResolvePeer(dialog.Id, dialog.Kind), text);
        _logger.Info("Telegram", $"消息发送成功：{dialog.Kind}/{dialog.Id}，字符数 {text.Length}");
    }

    public async Task SendScheduledAsync(ScheduledMessage schedule)
    {
        EnsureLogin();
        await _client!.SendMessageAsync(ResolvePeer(schedule.ChatId, schedule.ChatKind), schedule.Message);
        _logger.Info("Telegram", $"定时消息发送成功：{schedule.ChatKind}/{schedule.ChatId}，任务 {schedule.Id}");
    }

    public async Task SendConfirmationAsync(ScheduledMessage schedule, string text)
    {
        EnsureLogin();
        if (schedule.ConfirmationPeerId is not long id) return;
        await _client!.SendMessageAsync(ResolvePeer(id, schedule.ConfirmationPeerKind), text);
        _logger.Info("Telegram", $"完成确认发送成功：{schedule.ConfirmationPeerKind}/{id}，任务 {schedule.Id}");
    }

    private InputPeer ResolvePeer(long id, string kind)
    {
        if (_peers.TryGetValue(PeerKey(id, kind), out var peer)) return peer;
        if (kind == "User" && _manager!.Users.TryGetValue(id, out var user)) return user.ToInputPeer();
        if (kind == "Chat") return new InputPeerChat(id);
        if (_manager!.Chats.TryGetValue(id, out var chat)) return chat.ToInputPeer();
        throw new InvalidOperationException($"找不到会话 {id}，请刷新会话后再试");
    }

    private Task OnUpdate(Update update)
    {
        TLMessage? message = update switch
        {
            UpdateNewMessage x => x.message as TLMessage,
            UpdateEditMessage x => x.message as TLMessage,
            _ => null
        };
        if (message is null) return Task.CompletedTask;

        var peerInfo = _manager?.UserOrChat(message.peer_id);
        var isGroup = peerInfo is ChatBase;
        var chatName = peerInfo switch
        {
            ChatBase chat => chat.Title ?? chat.ID.ToString(),
            User user => DisplayName(user),
            _ => $"ID {message.peer_id.ID}"
        };
        MessageReceived?.Invoke(new(
            message.date.ToLocalTime(), chatName, NameOf(message.from_id),
            string.IsNullOrWhiteSpace(message.message) ? "[媒体消息]" : message.message,
            isGroup, message.peer_id.ID,
            message.flags.HasFlag(TLMessage.Flags.out_), IsMentioned(message)));
        return Task.CompletedTask;
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
    }
}
