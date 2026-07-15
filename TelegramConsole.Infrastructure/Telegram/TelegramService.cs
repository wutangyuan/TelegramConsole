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

    public bool IsLoggedIn => _client?.User is not null;
    public long CurrentUserId => _client?.User?.id ?? 0;
    public string CurrentUser => _client?.User?.ToString() ?? "未登录";
    public event Action<ChatLine>? MessageReceived;
    public event Action<string>? Log;

    public TelegramService(ISettingsStore store, IAppLogger logger)
    {
        _store = store;
        _logger = logger;
        WTelegram.Helpers.Log = (level, message) =>
        {
            _logger.Write(level >= 4 ? AppLogLevel.Warning : AppLogLevel.Debug, "WTelegram", message);
            if (level >= 3) Log?.Invoke(message);
        };
    }

    public async Task<string?> BeginLoginAsync(AppSettings settings)
    {
        _logger.Info("Telegram", "开始连接并登录 Telegram");
        _client?.Dispose();
        _client = new WTelegram.Client(settings.ApiId, settings.ApiHash, _store.GetSessionPath(settings.PhoneNumber));
        ApplyProxy(_client, settings.Proxy);
        _manager = _client.WithUpdateManager(OnUpdate);
        var prompt = await _client.Login(settings.PhoneNumber);
        _logger.Info("Telegram", prompt is null ? "Telegram 登录成功" : $"Telegram 登录等待输入：{prompt}");
        return prompt;
    }

    public async Task<string?> ContinueLoginAsync(string value)
    {
        if (_client is null) throw new InvalidOperationException("请先点击登录");
        var prompt = await _client.Login(value);
        _logger.Info("Telegram", prompt is null ? "Telegram 登录成功" : $"Telegram 登录继续等待输入：{prompt}");
        return prompt;
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

    private static string PeerKey(long id, string kind) => $"{kind}:{id}";

    public void Dispose()
    {
        _logger.Info("Telegram", "Telegram 服务正在关闭");
        _client?.Dispose();
    }
}
