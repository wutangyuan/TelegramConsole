using Quartz;
using Quartz.Impl;
using Quartz.Impl.Matchers;
using TelegramConsole.Core;

namespace TelegramConsole.Infrastructure;

public sealed class SchedulerService : ISchedulerService
{
    private const string ContextKey = "TelegramConsoleApp.SchedulerService";
    private readonly ITelegramService _telegram;
    private readonly ISettingsStore _store;
    private readonly IAppLogger _logger;
    private readonly AppSettings _settings;
    private readonly Task _initialization;
    private IScheduler? _scheduler;
    private AccountProfile? _activeAccount;
    private bool _disposed;

    public event Action<string>? Status;

    public SchedulerService(ITelegramService telegram, ISettingsStore store, AppSettings settings, IAppLogger logger)
    {
        _telegram = telegram;
        _store = store;
        _settings = settings;
        _logger = logger;
        _initialization = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        _scheduler = await StdSchedulerFactory.GetDefaultScheduler();
        _scheduler.Context[ContextKey] = this;
        await _scheduler.Start();
        Report(AppLogLevel.Information, "Quartz.NET 已启动，等待登录后加载账号任务");
    }

    public async Task ActivateAccountAsync(AccountProfile account)
    {
        await _initialization;
        if (_scheduler is null) return;
        await ClearAccountJobsAsync();
        _activeAccount = account;
        foreach (var task in account.Schedules) await UpsertInternalAsync(task);
        Report(AppLogLevel.Information,
            $"已切换账号 {account.UserId}，加载 {account.Schedules.Count(x => x.Enabled)} 个定时任务");
    }

    public async Task DeactivateAccountAsync()
    {
        await _initialization;
        await ClearAccountJobsAsync();
        _activeAccount = null;
    }

    private async Task ClearAccountJobsAsync()
    {
        if (_scheduler is null) return;
        foreach (var jobKey in await _scheduler.GetJobKeys(GroupMatcher<JobKey>.GroupEquals("telegram-schedules")))
            await _scheduler.DeleteJob(jobKey);
    }

    public async Task UpsertAsync(ScheduledMessage task)
    {
        await _initialization;
        await UpsertInternalAsync(task);
    }

    public async Task DeleteAsync(Guid taskId)
    {
        await _initialization;
        if (_scheduler is not null) await _scheduler.DeleteJob(JobKeyFor(taskId));
    }

    private async Task UpsertInternalAsync(ScheduledMessage task)
    {
        if (_scheduler is null) return;
        var jobKey = JobKeyFor(task.Id);
        if (await _scheduler.CheckExists(jobKey)) await _scheduler.DeleteJob(jobKey);
        if (!task.Enabled) return;

        var job = JobBuilder.Create<ScheduledMessageJob>()
            .WithIdentity(jobKey)
            .UsingJobData("TaskId", task.Id.ToString("D"))
            .Build();
        var scheduleBuilder = task.Period == SchedulePeriod.Weekly
            ? CronScheduleBuilder.CronSchedule(BuildWeeklyCron(task))
            : CronScheduleBuilder.DailyAtHourAndMinute(task.Time.Hours, task.Time.Minutes);
        var trigger = TriggerBuilder.Create()
            .WithIdentity($"telegram-trigger-{task.Id:N}", "telegram-schedules")
            .ForJob(job)
            .WithSchedule(scheduleBuilder
                .InTimeZone(TimeZoneInfo.Local)
                .WithMisfireHandlingInstructionFireAndProceed())
            .Build();
        await _scheduler.ScheduleJob(job, trigger);
    }

    public async Task RunDueTasksAsync()
    {
        await _initialization;
        if (!_telegram.IsLoggedIn) return;
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);
        if (_activeAccount is null) return;
        foreach (var task in _activeAccount.Schedules.Where(x =>
                     x.Enabled && x.LastSentDate != today && IsScheduledForDate(x, today) && now.TimeOfDay >= x.Time).ToArray())
            await ExecuteTaskAsync(task.Id, true);
    }

    public async Task ExecuteNowAsync(Guid taskId)
    {
        await _initialization;
        await ExecuteTaskAsync(taskId, isManual: true);
    }

    internal async Task ExecuteTaskAsync(Guid taskId, bool isRecovery = false, bool isManual = false)
    {
        var task = _activeAccount?.Schedules.FirstOrDefault(x => x.Id == taskId);
        if (task is null || (!task.Enabled && !isManual)) return;
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);
        if (task.LastSentDate == today && !isManual) return;
        if (!_telegram.IsLoggedIn)
        {
            if (isManual) throw new InvalidOperationException("请先登录 Telegram，再立即执行任务");
            Report(AppLogLevel.Warning, $"Quartz 任务等待登录后补发：{task.ChatTitle}");
            return;
        }

        try
        {
            await _telegram.SendScheduledAsync(task);
            task.LastSentDate = today;
            _store.Save(_settings);
            var executionType = isManual ? " 手动" : isRecovery ? " 补发" : "";
            Report(AppLogLevel.Information, $"Quartz{executionType}任务已发送：{task.ChatTitle}（任务 {task.Id}）");
        }
        catch (Exception ex)
        {
            Report(AppLogLevel.Error, $"Quartz 任务失败：{task.ChatTitle}", ex);
            throw;
        }

        var confirmation = FormatConfirmation(task, now);
        if (task.ConfirmationPeerId is not null)
            await TryNotifyAsync(
                () => _telegram.SendConfirmationAsync(task, confirmation),
                $"完成确认已发送到 Telegram：{task.ConfirmationPeerTitle}",
                "Telegram 完成确认发送失败");
        if (!string.IsNullOrWhiteSpace(task.ConfirmationEmail))
            await TryNotifyAsync(
                () => EmailNotificationService.SendAsync(
                    _settings.Email, task.ConfirmationEmail,
                    $"Telegram 签到完成 - {task.ChatTitle}", confirmation),
                $"完成确认邮件已发送到：{task.ConfirmationEmail}",
                "完成确认邮件发送失败");
    }

    private async Task TryNotifyAsync(Func<Task> action, string success, string failure)
    {
        try
        {
            await action();
            Report(AppLogLevel.Information, success);
        }
        catch (Exception ex)
        {
            Report(AppLogLevel.Warning, failure, ex);
        }
    }

    private static JobKey JobKeyFor(Guid id) => new($"telegram-job-{id:N}", "telegram-schedules");

    private void Report(AppLogLevel level, string message, Exception? exception = null)
    {
        _logger.Write(level, "Scheduler", message, exception);
        Status?.Invoke(exception is null ? message : $"{message}：{exception.Message}");
    }
    private static bool IsScheduledForDate(ScheduledMessage task, DateOnly date) =>
        task.Period == SchedulePeriod.Daily || task.WeekDays.Contains(date.DayOfWeek);

    private static string BuildWeeklyCron(ScheduledMessage task)
    {
        if (task.WeekDays.Count == 0) throw new InvalidOperationException("每周任务至少需要选择一天");
        var days = string.Join(',', task.WeekDays.Distinct().OrderBy(x => x).Select(x => x switch
        {
            DayOfWeek.Sunday => "SUN", DayOfWeek.Monday => "MON", DayOfWeek.Tuesday => "TUE",
            DayOfWeek.Wednesday => "WED", DayOfWeek.Thursday => "THU", DayOfWeek.Friday => "FRI",
            DayOfWeek.Saturday => "SAT", _ => throw new ArgumentOutOfRangeException()
        }));
        return $"0 {task.Time.Minutes} {task.Time.Hours} ? * {days} *";
    }

    private static string FormatConfirmation(ScheduledMessage task, DateTime time) =>
        task.ConfirmationText
            .Replace("{群聊}", task.ChatTitle, StringComparison.Ordinal)
            .Replace("{时间}", time.ToString("yyyy-MM-dd HH:mm:ss"), StringComparison.Ordinal)
            .Replace("{内容}", task.Message, StringComparison.Ordinal);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_scheduler is not null) _ = _scheduler.Shutdown(false);
    }

    [DisallowConcurrentExecution]
    public sealed class ScheduledMessageJob : IJob
    {
        public async Task Execute(IJobExecutionContext context)
        {
            var rawId = context.MergedJobDataMap.GetString("TaskId");
            if (!Guid.TryParse(rawId, out var taskId)) return;
            var service = context.Scheduler.Context[ContextKey] as SchedulerService;
            if (service is null) return;
            try
            {
                await service.ExecuteTaskAsync(taskId);
            }
            catch (Exception ex)
            {
                throw new JobExecutionException(ex, refireImmediately: false);
            }
        }
    }
}
