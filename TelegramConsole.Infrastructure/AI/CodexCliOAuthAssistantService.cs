using System.Diagnostics;
using TelegramConsole.Core;

namespace TelegramConsole.AI;

/// <summary>
/// Adapts the user's local Codex CLI login for text-only assistant features.
/// Credentials remain owned by Codex in its own authenticated credential store.
/// </summary>
public sealed class CodexCliOAuthAssistantService
{
    private readonly IAppLogger? _logger;
    public CodexCliOAuthAssistantService(IAppLogger? logger) => _logger = logger;

    public async Task<AiTextResult> CompleteAsync(AiAssistantSettings settings, string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        if (!settings.Enabled)
            throw new InvalidOperationException("全局 AI 尚未启用，请前往“管理中心 → 设置 → AI 助手”完成配置");

        var cli = FindCodexCli();
        if (cli is null)
            throw new InvalidOperationException("未找到 Codex CLI。请先安装 Codex 并使用 ChatGPT/Codex 账户登录，然后在本页重新测试。");

        var temp = Path.Combine(Path.GetTempPath(), $"telegram-console-codex-{Guid.NewGuid():N}.txt");
        try
        {
            var prompt = $"{systemPrompt}\n\n{userPrompt}\n\n仅输出最终文本答案；不要使用工具、不要读取或修改文件。";
            var start = new ProcessStartInfo
            {
                FileName = cli,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath()
            };
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add("--ephemeral");
            start.ArgumentList.Add("--skip-git-repo-check");
            start.ArgumentList.Add("--sandbox");
            start.ArgumentList.Add("read-only");
            start.ArgumentList.Add("--output-last-message");
            start.ArgumentList.Add(temp);
            if (!string.IsNullOrWhiteSpace(settings.Model))
            {
                start.ArgumentList.Add("--model");
                start.ArgumentList.Add(settings.Model.Trim());
            }
            start.ArgumentList.Add(prompt);

            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Codex CLI");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 20, 300)));
            // Read both redirected streams immediately; otherwise a verbose CLI progress stream can
            // fill its pipe and block before the process exits.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
            {
                _logger?.Warning("AI", $"Codex CLI 请求失败，退出码 {process.ExitCode}");
                throw new InvalidOperationException(BuildCliFailure(stderr));
            }
            var stdout = await stdoutTask;
            var text = File.Exists(temp) ? await File.ReadAllTextAsync(temp, timeout.Token) : stdout;
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Codex CLI 没有返回可用文本");
            return new AiTextResult(text.Trim(), string.IsNullOrWhiteSpace(settings.Model) ? "Codex" : settings.Model.Trim(), "CodexCliOAuth");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Codex 响应超时，请稍后重试或在 AI 设置中提高超时时间。");
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* temporary output cleanup only */ }
        }
    }

    public static string? FindCodexCli()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new[]
        {
            Path.Combine(appData, "npm", "codex.cmd"),
            Path.Combine(appData, "npm", "codex.exe"),
            "codex"
        };
        return candidates.FirstOrDefault(path => path == "codex" || File.Exists(path));
    }

    private static string BuildCliFailure(string stderr)
    {
        var detail = stderr.Trim();
        if (detail.Contains("login", StringComparison.OrdinalIgnoreCase) || detail.Contains("auth", StringComparison.OrdinalIgnoreCase))
            return "Codex 登录状态不可用或已过期。请在“AI 助手”页点击“登录 ChatGPT/Codex”，完成授权后再试。";
        return string.IsNullOrWhiteSpace(detail) ? "Codex CLI 调用失败，请确认已完成 ChatGPT/Codex 登录。" : $"Codex CLI 调用失败：{detail[..Math.Min(detail.Length, 500)]}";
    }
}
