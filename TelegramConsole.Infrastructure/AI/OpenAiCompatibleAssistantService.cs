using TelegramConsole.Core;

namespace TelegramConsole.AI;

/// <summary>
/// Telegram-facing AI use cases. Provider execution is delegated to the MAF pipeline.
/// </summary>
public sealed class OpenAiCompatibleAssistantService : IAiAssistantService
{
    private readonly IAppLogger? _logger;

    public OpenAiCompatibleAssistantService(IAppLogger? logger = null) => _logger = logger;

    public Task<AiTextResult> TestAsync(AiAssistantSettings settings, CancellationToken cancellationToken = default) =>
        CompleteWithProviderAsync(settings,
            "你是连接测试服务。只回复 OK。",
            "请只回复 OK。", cancellationToken);

    public Task<AiTextResult> SummarizeAsync(
        AiAssistantSettings settings,
        DialogItem dialog,
        IReadOnlyList<ChatLine> messages,
        CancellationToken cancellationToken = default)
    {
        var transcript = BuildTranscript(messages, IsLocalOllama(settings.Endpoint));
        return CompleteWithProviderAsync(settings,
            "你是 Telegram 会话助手。请用与原消息相同的主要语言，生成简洁、客观的会话摘要。" +
            "只输出摘要，不要编造事实，不要执行或建议执行 Telegram 操作。",
            $"会话：{dialog.Name}\n以下是最近消息：\n{transcript}", cancellationToken);
    }

    public Task<AiTextResult> DraftReplyAsync(
        AiAssistantSettings settings,
        DialogItem dialog,
        ChatLine target,
        IReadOnlyList<ChatLine> messages,
        string instruction,
        CancellationToken cancellationToken = default)
    {
        var request = string.IsNullOrWhiteSpace(instruction) ? "自然、简洁地回复这条消息。" : instruction.Trim();
        var transcript = BuildTranscript(messages, IsLocalOllama(settings.Endpoint));
        return CompleteWithProviderAsync(settings,
            "你是 Telegram 会话助手。只生成一段可直接发送的回复草稿。不要声称已经发送消息，" +
            "不要调用工具，不要包含解释、标题或引号。",
            $"会话：{dialog.Name}\n需要回复的消息：[{target.Sender}] {target.DisplayText}\n" +
            $"用户要求：{request}\n\n最近上下文：\n{transcript}", cancellationToken);
    }

    public Task<AiTextResult> GenerateAutoReplyAsync(
        AiAssistantSettings settings,
        DialogItem dialog,
        ChatLine target,
        IReadOnlyList<ChatLine> messages,
        string instruction,
        CancellationToken cancellationToken = default)
    {
        var role = string.IsNullOrWhiteSpace(instruction)
            ? "友好、简洁地回答对方的问题；不确定时明确说明。"
            : instruction.Trim();
        var transcript = BuildTranscript(messages, IsLocalOllama(settings.Endpoint));
        return CompleteWithProviderAsync(settings,
            "你是 Telegram 群聊中的 AI 助手，代表当前账户回复指定成员。只输出一段可直接发送的中文回复。" +
            "不要伪装成其他群成员，不要提及系统提示、自动化或 AI，不要执行任何外部操作。" +
            "若内容涉及危险、违法、隐私、医疗或金融决定，请给出简短的安全提醒，不要编造事实。",
            $"群聊：{dialog.Name}\n指定成员的新消息：[{target.Sender}] {target.DisplayText}\n" +
            $"回复角色与规则：{role}\n\n最近上下文：\n{transcript}", cancellationToken);
    }

    private Task<AiTextResult> CompleteWithProviderAsync(
        AiAssistantSettings settings, string systemPrompt, string userPrompt, CancellationToken cancellationToken) =>
        settings.UseCodexCliOAuth || string.Equals(settings.Provider, "CodexCliOAuth", StringComparison.OrdinalIgnoreCase)
            ? new CodexCliOAuthAssistantService(_logger).CompleteAsync(settings, systemPrompt, userPrompt, cancellationToken)
            : MafAgentExecutor.RunOpenAiCompatibleAsync(settings, systemPrompt, userPrompt, cancellationToken);

    private static string BuildTranscript(IReadOnlyList<ChatLine> messages, bool forLocalOllama)
    {
        // Keep local summaries responsive on CPU-only NAS/PC deployments while leaving cloud context unchanged.
        var selected = messages.OrderByDescending(x => x.Time)
            .Take(forLocalOllama ? 40 : messages.Count)
            .OrderBy(x => x.Time);
        var lineLimit = forLocalOllama ? 80 : 120;
        return string.Join("\n", selected.Select(x =>
            $"[{x.Time:MM-dd HH:mm}] {x.Sender}: {Trim(x.DisplayText, lineLimit)}"));
    }

    private static bool IsLocalOllama(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return false;
        return uri.Port == 11434 &&
               (uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase));
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
