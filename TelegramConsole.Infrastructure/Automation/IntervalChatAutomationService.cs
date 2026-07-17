using System.Text;
using TelegramConsole.Core;

namespace TelegramConsole.Infrastructure;

public sealed class IntervalChatAutomationService : IIntervalChatAutomationService
{
    private readonly ITelegramService _telegram;
    private readonly ISettingsStore _store;
    private readonly IAppLogger _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _executionLock = new(1, 1);
    private readonly Task _worker;
    private AccountProfile? _activeAccount;
    private bool _disposed;

    public event Action<string>? Status;

    public IntervalChatAutomationService(ITelegramService telegram, ISettingsStore store, IAppLogger logger)
    {
        _telegram = telegram;
        _store = store;
        _logger = logger;
        _worker = Task.Run(() => WorkerAsync(_shutdown.Token));
    }

    public Task ActivateAccountAsync(AccountProfile account)
    {
        ArgumentNullException.ThrowIfNull(account);
        _activeAccount = account;
        var now = DateTimeOffset.Now;
        foreach (var rule in account.IntervalChatRules)
        {
            Normalize(rule);
            rule.WindowStartedAt ??= now;
            rule.LastCheckedAt ??= now;
        }
        _store.SaveAccount(account);
        Report($"已加载 {account.IntervalChatRules.Count(x => x.Enabled)} 个间隔分析任务");
        return Task.CompletedTask;
    }

    public Task DeactivateAccountAsync()
    {
        _activeAccount = null;
        return Task.CompletedTask;
    }

    public Task UpsertAsync(IntervalChatRule rule)
    {
        Normalize(rule);
        var now = DateTimeOffset.Now;
        rule.WindowStartedAt ??= now;
        rule.LastCheckedAt ??= now;
        var account = _activeAccount ?? throw new InvalidOperationException("请先激活 Telegram 账户");
        var index = account.IntervalChatRules.FindIndex(x => x.Id == rule.Id);
        if (index < 0) account.IntervalChatRules.Add(rule); else account.IntervalChatRules[index] = rule;
        Persist();
        Report($"间隔分析任务已保存：{rule.Name}");
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid ruleId)
    {
        var account = _activeAccount;
        if (account is not null)
        {
            account.IntervalChatRules.RemoveAll(x => x.Id == ruleId);
            _store.SaveAccount(account);
        }
        return Task.CompletedTask;
    }

    public async Task ExecuteNowAsync(Guid ruleId)
    {
        var rule = _activeAccount?.IntervalChatRules.FirstOrDefault(x => x.Id == ruleId)
            ?? throw new InvalidOperationException("找不到间隔分析任务");
        await ExecuteRuleAsync(rule, DateTimeOffset.Now, manual: true, _shutdown.Token);
    }

    private async Task WorkerAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await RunDueRulesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.Error("IntervalChat", "间隔分析后台循环异常退出", ex);
        }
    }

    private async Task RunDueRulesAsync(CancellationToken cancellationToken)
    {
        var account = _activeAccount;
        if (account is null || !_telegram.IsLoggedIn) return;
        var now = DateTimeOffset.Now;
        foreach (var rule in account.IntervalChatRules.Where(x => x.Enabled).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            Normalize(rule);
            var lastCheck = rule.LastCheckedAt ?? rule.WindowStartedAt ?? now;
            if (lastCheck.AddMinutes(rule.IntervalMinutes) > now) continue;
            try { await ExecuteRuleAsync(rule, now, manual: false, cancellationToken); }
            catch (Exception) { /* Failure is logged and persisted per rule; other rules must continue. */ }
        }
    }

    private async Task ExecuteRuleAsync(
        IntervalChatRule rule,
        DateTimeOffset now,
        bool manual,
        CancellationToken cancellationToken)
    {
        await _executionLock.WaitAsync(cancellationToken);
        try
        {
            if (!_telegram.IsLoggedIn) throw new InvalidOperationException("Telegram 账户当前不在线");
            Normalize(rule);
            var windowStart = rule.WindowStartedAt ?? now;
            var source = new DialogItem(
                rule.SourceChatTitle, rule.SourceChatId, rule.SourceChatKind,
                !string.Equals(rule.SourceChatKind, "User", StringComparison.Ordinal));
            var history = await _telegram.LoadHistoryAsync(source, 300);
            var eligible = history
                .Where(x => !x.IsOutgoing && x.Time >= windowStart.LocalDateTime && x.Time <= now.LocalDateTime)
                .OrderBy(x => x.Time)
                .ThenBy(x => x.MessageId)
                .ToArray();

            rule.LastCheckedAt = now;
            rule.LastObservedMessageCount = eligible.Length;
            if (eligible.Length < rule.MinimumMessageCount)
            {
                rule.LastStatus = $"消息不足：{eligible.Length}/{rule.MinimumMessageCount}，继续累计";
                Persist();
                Report($"{rule.Name}：{rule.LastStatus}");
                return;
            }

            var target = new DialogItem(
                rule.TargetChatTitle, rule.TargetChatId, rule.TargetChatKind,
                !string.Equals(rule.TargetChatKind, "User", StringComparison.Ordinal));
            var digest = BuildDigest(rule, eligible, windowStart, now);
            await _telegram.SendAsync(target, digest);
            rule.LastSentAt = now;
            rule.WindowStartedAt = now;
            rule.LastCheckedAt = now;
            rule.LastStatus = $"已发送，共分析 {eligible.Length} 条消息";
            Persist();
            Report($"{rule.Name}：{rule.LastStatus}，目标 {rule.TargetChatTitle}{(manual ? "（手动检查）" : "")}");
        }
        catch (Exception ex)
        {
            rule.LastCheckedAt = now;
            rule.LastStatus = $"执行失败：{ex.Message}";
            Persist();
            _logger.Error("IntervalChat", $"间隔分析任务失败：{rule.Name}", ex);
            Status?.Invoke($"{rule.Name}：{rule.LastStatus}");
            throw;
        }
        finally { _executionLock.Release(); }
    }

    private static string BuildDigest(
        IntervalChatRule rule,
        IReadOnlyList<ChatLine> messages,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        var participants = messages
            .GroupBy(x => string.IsNullOrWhiteSpace(x.Sender) ? "未知" : x.Sender)
            .OrderByDescending(x => x.Count())
            .ThenBy(x => x.Key)
            .ToArray();
        var builder = new StringBuilder()
            .AppendLine($"【{rule.Name}】")
            .AppendLine($"来源：{rule.SourceChatTitle}")
            .AppendLine($"时段：{from.LocalDateTime:MM-dd HH:mm} ～ {to.LocalDateTime:MM-dd HH:mm}")
            .AppendLine($"消息：{messages.Count} 条，参与者：{participants.Length} 人");
        if (participants.Length > 0)
            builder.AppendLine("活跃成员：" + string.Join("、", participants.Take(5).Select(x => $"{x.Key}({x.Count()})")));
        builder.AppendLine().AppendLine("最近讨论：");
        foreach (var message in messages.TakeLast(rule.SummaryLineCount))
        {
            var text = message.DisplayText.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (text.Length > 180) text = text[..177] + "...";
            if (text.Length == 0) continue;
            builder.AppendLine($"- {message.Sender}：{text}");
        }
        var result = builder.ToString().TrimEnd();
        return result.Length <= 3900 ? result : result[..3897] + "...";
    }

    private void Persist()
    {
        if (_activeAccount is not null) _store.SaveAccount(_activeAccount);
    }

    private void Report(string message)
    {
        _logger.Info("IntervalChat", message);
        Status?.Invoke(message);
    }

    private static void Normalize(IntervalChatRule rule)
    {
        rule.Name = string.IsNullOrWhiteSpace(rule.Name) ? "聊天简报" : rule.Name.Trim();
        rule.IntervalMinutes = Math.Clamp(rule.IntervalMinutes, 1, 1440);
        rule.MinimumMessageCount = Math.Clamp(rule.MinimumMessageCount, 1, 300);
        rule.SummaryLineCount = Math.Clamp(rule.SummaryLineCount, 1, 30);
        if (rule.SourceChatId == 0 || rule.TargetChatId == 0)
            throw new InvalidOperationException("必须选择来源会话和发送目标");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        try { _worker.Wait(TimeSpan.FromSeconds(3)); } catch { }
        _shutdown.Dispose();
        _executionLock.Dispose();
    }
}
