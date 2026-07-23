using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;
using TelegramConsole.Core;

namespace TelegramConsole.AI;

/// <summary>
/// Microsoft Agent Framework entry point for OpenAI and OpenAI-compatible providers.
/// The provider endpoint only determines the underlying chat client; every request is
/// executed through the same MAF <see cref="AIAgent"/> pipeline.
/// </summary>
internal static class MafAgentExecutor
{
    public static async Task<AiTextResult> RunOpenAiCompatibleAsync(
        AiAssistantSettings settings,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken)
    {
        if (!settings.Enabled)
            throw new InvalidOperationException("请先启用全局 AI 助手");
        if (string.IsNullOrWhiteSpace(settings.ApiKey) && !IsLocalOllama(settings))
            throw new InvalidOperationException("请在管理中心 → 设置 → AI 助手中配置 API Key");
        if (string.IsNullOrWhiteSpace(settings.Model))
            throw new InvalidOperationException("请配置 AI 模型名称");
        if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("请配置有效的 AI 接口地址");

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeoutSeconds = IsLocalOllama(settings)
            ? Math.Max(settings.TimeoutSeconds, 300)
            : Math.Clamp(settings.TimeoutSeconds, 10, 180);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 10, 300)));
        var apiKey = string.IsNullOrWhiteSpace(settings.ApiKey) ? "ollama" : settings.ApiKey;
        var client = new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = endpoint });
        var chatClient = client.GetChatClient(settings.Model.Trim()).AsIChatClient();
        AIAgent agent = chatClient.AsAIAgent(
            name: "TelegramConsoleAssistant",
            instructions: systemPrompt);
        try
        {
            var response = await agent.RunAsync(userPrompt, cancellationToken: timeoutSource.Token);
            var text = response.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("MAF Agent 没有返回可用文本");
            return new AiTextResult(text, settings.Model.Trim(), "MicrosoftAgentFramework/OpenAICompatible");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var detail = IsLocalOllama(settings)
                ? "本地 Ollama 在 300 秒内未完成推理。模型可能正在首次加载、设备性能不足或摘要上下文过长；请稍后重试，或在 AI 设置中把上下文消息数调低。"
                : $"AI 服务在 {Math.Clamp(settings.TimeoutSeconds, 10, 180)} 秒内未返回结果";
            throw new TimeoutException(detail);
        }
    }

    private static bool IsLocalOllama(AiAssistantSettings settings) =>
        Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint) &&
        endpoint.Port == 11434 &&
        (endpoint.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
         endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
         endpoint.Host.Equals("::1", StringComparison.OrdinalIgnoreCase));
}
