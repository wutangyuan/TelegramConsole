using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TelegramConsole.Core;

namespace TelegramConsole.Infrastructure;

/// <summary>
/// Small provider adapter for OpenAI-compatible chat APIs. It works with hosted providers and
/// local Ollama instances exposing /v1/chat/completions, without coupling account operations to a model.
/// </summary>
public sealed class OpenAiCompatibleAssistantService : IAiAssistantService
{
    private static readonly HttpClient HttpClient = new();
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
        var transcript = BuildTranscript(messages);
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
        var transcript = BuildTranscript(messages);
        return CompleteWithProviderAsync(settings,
            "你是 Telegram 会话助手。只生成一段可直接发送的回复草稿。不要声称已经发送消息，" +
            "不要调用工具，不要包含解释、标题或引号。",
            $"会话：{dialog.Name}\n需要回复的消息：[{target.Sender}] {target.DisplayText}\n" +
            $"用户要求：{request}\n\n最近上下文：\n{transcript}", cancellationToken);
    }

    private Task<AiTextResult> CompleteWithProviderAsync(
        AiAssistantSettings settings, string systemPrompt, string userPrompt, CancellationToken cancellationToken) =>
        settings.UseCodexCliOAuth || string.Equals(settings.Provider, "CodexCliOAuth", StringComparison.OrdinalIgnoreCase)
            ? new CodexCliOAuthAssistantService(_logger).CompleteAsync(settings, systemPrompt, userPrompt, cancellationToken)
            : CompleteAsync(settings, systemPrompt, userPrompt, cancellationToken);

    private async Task<AiTextResult> CompleteAsync(
        AiAssistantSettings settings, string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        Validate(settings);
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildCompletionUri(settings.Endpoint));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = settings.Model.Trim(),
            temperature = 0.3,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            }
        }), Encoding.UTF8, "application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 10, 180)));
        using var response = await HttpClient.SendAsync(request, timeout.Token);
        var body = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            var detail = ReadErrorMessage(body);
            var summary = BuildFailureMessage((int)response.StatusCode, response.ReasonPhrase, detail);
            _logger?.Warning("AI", $"AI 服务请求失败：HTTP {(int)response.StatusCode}");
            throw new InvalidOperationException(summary);
        }

        using var document = JsonDocument.Parse(body);
        var text = document.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0 &&
                   choices[0].TryGetProperty("message", out var message) &&
                   message.TryGetProperty("content", out var content)
            ? ReadContent(content)
            : "";
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("AI 服务没有返回可用文本");
        return new AiTextResult(text.Trim(), settings.Model.Trim(), settings.Provider);
    }

    private static string ReadContent(JsonElement content) => content.ValueKind switch
    {
        JsonValueKind.String => content.GetString() ?? "",
        JsonValueKind.Array => string.Join("", content.EnumerateArray()
            .Where(x => x.TryGetProperty("text", out _))
            .Select(x => x.GetProperty("text").GetString() ?? "")),
        _ => ""
    };

    private static string ReadErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("error", out var error)) return "";
            var message = error.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString() ?? ""
                : "";
            var code = error.TryGetProperty("code", out var codeElement)
                ? codeElement.ToString()
                : "";
            return string.IsNullOrWhiteSpace(code) || message.Contains(code, StringComparison.OrdinalIgnoreCase)
                ? message
                : $"{message}（{code}）";
        }
        catch (JsonException) { return ""; }
    }

    private static string BuildFailureMessage(int statusCode, string? reason, string detail)
    {
        if (statusCode == 429)
        {
            var guidance = detail.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
                           detail.Contains("billing", StringComparison.OrdinalIgnoreCase)
                ? "API 项目额度不足或账单未开通，请检查 OpenAI Platform 的 Billing/Usage。"
                : "请求频率或令牌量超限，请稍后重试并检查项目 Limits。";
            return string.IsNullOrWhiteSpace(detail)
                ? $"AI 服务拒绝请求（429）：{guidance}"
                : $"AI 服务拒绝请求（429）：{detail}\n{guidance}";
        }
        return string.IsNullOrWhiteSpace(detail)
            ? $"AI 服务请求失败：{statusCode} {reason}"
            : $"AI 服务请求失败：{statusCode} {detail}";
    }

    private static Uri BuildCompletionUri(string endpoint)
    {
        var normalized = endpoint.Trim().TrimEnd('/');
        if (normalized.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return new Uri(normalized);
        if (!normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) normalized += "/v1";
        return new Uri(normalized + "/chat/completions");
    }

    private static void Validate(AiAssistantSettings settings)
    {
        if (!settings.Enabled)
            throw new InvalidOperationException("全局 AI 尚未配置或未启用，请前往“管理中心 → 设置 → AI 助手”完成配置");
        if (!string.Equals(settings.Provider, "OpenAICompatible", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前仅支持 OpenAI 兼容接口");
        if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("全局 AI 接口地址未配置，请前往“管理中心 → 设置 → AI 助手”填写");
        if (string.IsNullOrWhiteSpace(settings.Model))
            throw new ArgumentException("全局 AI 模型名称未配置，请前往“管理中心 → 设置 → AI 助手”填写");
    }

    private static string BuildTranscript(IReadOnlyList<ChatLine> messages) =>
        string.Join("\n", messages.OrderBy(x => x.Time).Select(x =>
            $"[{x.Time:MM-dd HH:mm}] {x.Sender}: {Trim(x.DisplayText, 120)}"));

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
