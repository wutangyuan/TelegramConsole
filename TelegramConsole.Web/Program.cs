using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using TelegramConsole.Core;
using TelegramConsole.Infrastructure;
using TelegramConsole.Runtime;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var dataDirectory = builder.Configuration["TELEGRAMCONSOLE_DATA_DIR"] ??
                    Environment.GetEnvironmentVariable("TELEGRAMCONSOLE_DATA_DIR") ??
                    Path.Combine(builder.Environment.ContentRootPath, "data");
builder.Services.AddSingleton<ISettingsStore>(_ => new PortableSettingsStore(dataDirectory));
builder.Services.AddSingleton<IManagedAccountCatalog>(_ => new EncryptedManagedAccountCatalog(dataDirectory));
builder.Services.AddSingleton<AccountRuntimeManager>();
builder.Services.AddHostedService<AccountRuntimeHostedService>();
builder.Services.AddHealthChecks();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "TelegramConsole.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
    });

var app = builder.Build();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if (path.StartsWithSegments("/health") ||
        path.StartsWithSegments("/auth") ||
        path == "/login.html" ||
        path == "/styles.css")
    {
        await next();
        return;
    }

    if (context.User.Identity?.IsAuthenticated == true)
    {
        await next();
        return;
    }

    if (path.StartsWithSegments("/api"))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { title = "未登录", detail = "请先登录管理端", status = 401 });
        return;
    }

    context.Response.Redirect("/login.html");
});
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "0";
        return Task.CompletedTask;
    });
    await next();
});

app.UseStaticFiles();
app.MapGet("/", () => Results.Redirect("/index.html?v=20260717-settings"));
app.MapPost("/auth/login", async (AdminLoginInput request, HttpContext context) =>
{
    var configuredPassword = Environment.GetEnvironmentVariable("TELEGRAMCONSOLE_ADMIN_PASSWORD");
    if (string.IsNullOrWhiteSpace(configuredPassword))
        return Results.Problem("TELEGRAMCONSOLE_ADMIN_PASSWORD 未配置", statusCode: 503);

    if (request.Username != "admin" || !FixedTimeEquals(request.Password, configuredPassword))
        return Results.Json(new { title = "登录失败", detail = "账号或密码错误", status = 401 }, statusCode: 401);

    var identity = new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "admin")],
        CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity),
        new AuthenticationProperties { IsPersistent = request.RememberMe });
    return Results.Ok(new { username = "admin" });
});
app.MapPost("/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
});
app.MapHealthChecks("/health/live");
app.MapGet("/health/ready", (AccountRuntimeManager manager) =>
{
    var snapshots = manager.GetSnapshots();
    return Results.Ok(new
    {
        status = "ready",
        accounts = snapshots.Count,
        online = snapshots.Count(x => x.Status == AccountRuntimeStatus.Online),
        recovering = snapshots.Count(x => x.Status == AccountRuntimeStatus.Recovering),
        faulted = snapshots.Count(x => x.Status == AccountRuntimeStatus.Faulted)
    });
});

var api = app.MapGroup("/api");
api.MapGet("/settings/email", (AccountRuntimeManager manager) =>
{
    var email = manager.GetEmailSettings();
    return Results.Ok(new
    {
        email.SmtpHost,
        email.SmtpPort,
        email.EnableSsl,
        email.UserName,
        email.FromAddress,
        PasswordConfigured = !string.IsNullOrEmpty(email.Password)
    });
});
api.MapPut("/settings/email", (EmailSettingsInput request, AccountRuntimeManager manager) =>
{
    var host = request.SmtpHost?.Trim() ?? "";
    var from = request.FromAddress?.Trim() ?? "";
    var userName = request.UserName?.Trim() ?? "";
    if (request.SmtpPort is < 1 or > 65535) throw new ArgumentException("SMTP 端口必须是 1 到 65535");
    var hasAnyValue = host.Length > 0 || from.Length > 0 || userName.Length > 0 || !string.IsNullOrEmpty(request.Password);
    if (hasAnyValue && (host.Length == 0 || from.Length == 0))
        throw new ArgumentException("请至少填写 SMTP 服务器和发件地址");
    var previous = manager.GetEmailSettings();
    manager.SaveEmailSettings(new EmailSettings
    {
        SmtpHost = host,
        SmtpPort = request.SmtpPort,
        EnableSsl = request.EnableSsl,
        UserName = userName,
        Password = request.ClearPassword ? "" : string.IsNullOrEmpty(request.Password) ? previous.Password : request.Password,
        FromAddress = from
    });
    return Results.NoContent();
});
api.MapPost("/settings/email/test", async (EmailTestInput request, AccountRuntimeManager manager) =>
{
    var recipient = request.Recipient?.Trim() ?? "";
    if (recipient.Length == 0) throw new ArgumentException("请输入测试收件邮箱");
    await EmailNotificationService.SendAsync(
        manager.GetEmailSettings(), recipient, "TelegramConsole 邮件测试", $"TelegramConsole SMTP 配置测试成功。\n时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
    return Results.Ok(new { message = $"测试邮件已发送到 {recipient}" });
});
api.MapPost("/system/proxy-test", async (ProxyTestInput request, CancellationToken cancellationToken) =>
{
    var host = request.Host?.Trim() ?? "";
    if (host.Length == 0) throw new ArgumentException("代理地址不能为空");
    if (request.Port is < 1 or > 65535) throw new ArgumentException("代理端口必须是 1 到 65535");
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(8));
    var stopwatch = Stopwatch.StartNew();
    using var client = new TcpClient();
    try { await client.ConnectAsync(host, request.Port, timeout.Token); }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        throw new TimeoutException($"连接 {host}:{request.Port} 超时");
    }
    return Results.Ok(new { message = $"容器可以连接 {host}:{request.Port}", elapsedMilliseconds = stopwatch.ElapsedMilliseconds });
});
api.MapGet("/system", (AccountRuntimeManager manager) => Results.Ok(new
{
    name = "TelegramConsole NAS",
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0",
    startedAt = SystemStart.StartedAt,
    accounts = manager.GetSnapshots().Count,
    online = manager.GetSnapshots().Count(x => x.Status == AccountRuntimeStatus.Online)
}));
api.MapGet("/accounts", (AccountRuntimeManager manager) => manager.GetSnapshots());
api.MapPost("/accounts", async (CreateManagedAccountRequest request, AccountRuntimeManager manager, CancellationToken ct) =>
    Results.Ok(await manager.AddAsync(request, ct)));
api.MapPost("/accounts/{id:guid}/start", async (Guid id, AccountRuntimeManager manager, CancellationToken ct) =>
    Results.Ok(await manager.StartAsync(id, ct)));
api.MapPost("/accounts/{id:guid}/login", async (Guid id, LoginInput request, AccountRuntimeManager manager, CancellationToken ct) =>
    Results.Ok(await manager.ContinueLoginAsync(id, request.Value, ct)));
api.MapPost("/accounts/{id:guid}/stop", async (Guid id, AccountRuntimeManager manager) =>
{
    await manager.StopAsync(id);
    return Results.NoContent();
});
api.MapDelete("/accounts/{id:guid}", async (Guid id, AccountRuntimeManager manager) =>
{
    await manager.RemoveAsync(id);
    return Results.NoContent();
});
api.MapGet("/accounts/{id:guid}/dialogs", async (Guid id, AccountRuntimeManager manager) =>
    Results.Ok(await manager.LoadDialogsAsync(id)));
api.MapGet("/accounts/{id:guid}/history", async (
    Guid id, long dialogId, string dialogKind, string dialogName, bool isGroup,
    int? limit, AccountRuntimeManager manager) =>
    Results.Ok(await manager.LoadHistoryAsync(
        id, new DialogItem(dialogName, dialogId, dialogKind, isGroup), limit ?? 300)));
api.MapGet("/accounts/{id:guid}/messages", (Guid id, int? limit, AccountRuntimeManager manager) =>
    manager.GetRecentMessages(id, limit ?? 300));
api.MapPost("/accounts/{id:guid}/messages", async (Guid id, SendChatMessageRequest request, AccountRuntimeManager manager) =>
{
    await manager.SendAsync(id, request);
    return Results.Accepted();
});
api.MapGet("/accounts/{id:guid}/schedules", (Guid id, AccountRuntimeManager manager) => manager.GetSchedules(id));
api.MapPost("/accounts/{id:guid}/schedules", async (Guid id, ScheduleInput request, AccountRuntimeManager manager) =>
{
    var schedule = request.ToModel();
    await manager.UpsertScheduleAsync(id, schedule);
    return Results.Ok(schedule);
});
api.MapDelete("/accounts/{id:guid}/schedules/{scheduleId:guid}", async (Guid id, Guid scheduleId, AccountRuntimeManager manager) =>
{
    await manager.DeleteScheduleAsync(id, scheduleId);
    return Results.NoContent();
});
api.MapPost("/accounts/{id:guid}/schedules/{scheduleId:guid}/execute", async (Guid id, Guid scheduleId, AccountRuntimeManager manager) =>
{
    await manager.ExecuteScheduleAsync(id, scheduleId);
    return Results.Accepted();
});
api.MapGet("/accounts/{id:guid}/interval-rules", (Guid id, AccountRuntimeManager manager) =>
    manager.GetIntervalChatRules(id));
api.MapPost("/accounts/{id:guid}/interval-rules", async (Guid id, IntervalChatRuleInput request, AccountRuntimeManager manager) =>
{
    var rule = request.ToModel();
    await manager.UpsertIntervalChatRuleAsync(id, rule);
    return Results.Ok(rule);
});
api.MapDelete("/accounts/{id:guid}/interval-rules/{ruleId:guid}", async (Guid id, Guid ruleId, AccountRuntimeManager manager) =>
{
    await manager.DeleteIntervalChatRuleAsync(id, ruleId);
    return Results.NoContent();
});
api.MapPost("/accounts/{id:guid}/interval-rules/{ruleId:guid}/execute", async (Guid id, Guid ruleId, AccountRuntimeManager manager) =>
{
    await manager.ExecuteIntervalChatRuleAsync(id, ruleId);
    return Results.Accepted();
});
api.MapGet("/accounts/{id:guid}/logs", (
    Guid id, string? level, string? keyword, int? limit, AccountRuntimeManager manager) =>
{
    AppLogLevel? parsedLevel = null;
    if (!string.IsNullOrWhiteSpace(level))
    {
        if (!Enum.TryParse<AppLogLevel>(level, true, out var value))
            throw new ArgumentException("日志级别无效");
        parsedLevel = value;
    }
    return manager.GetRuntimeLogs(id, parsedLevel, keyword ?? "", limit ?? 300);
});
api.MapGet("/accounts/{id:guid}/exceptions", async (
    Guid id, DateTimeOffset? from, DateTimeOffset? to, string? level, string? keyword, int? limit,
    AccountRuntimeManager manager) =>
{
    AppLogLevel? parsedLevel = null;
    if (!string.IsNullOrWhiteSpace(level))
    {
        if (!Enum.TryParse<AppLogLevel>(level, true, out var value))
            throw new ArgumentException("异常级别无效");
        parsedLevel = value;
    }
    return Results.Ok(await manager.QueryExceptionsAsync(
        id, new ExceptionQuery(from, to, parsedLevel, keyword ?? "", limit ?? 200)));
});
api.MapPost("/accounts/{id:guid}/exceptions/retry", async (
    Guid id, RecordIdsInput request, AccountRuntimeManager manager) =>
{
    await manager.RetryExceptionNotificationsAsync(id, request.Ids.Distinct());
    return Results.Accepted();
});
api.MapGet("/accounts/{id:guid}/mentions", async (
    Guid id, string? keyword, int? limit, AccountRuntimeManager manager) =>
    Results.Ok(await manager.QueryMentionsAsync(id, new MentionQuery(keyword ?? "", limit ?? 300))));
api.MapGet("/accounts/{id:guid}/outbox", async (Guid id, int? limit, AccountRuntimeManager manager) =>
    Results.Ok(await manager.QueryOutboxAsync(id, limit ?? 300)));
api.MapPost("/accounts/{id:guid}/outbox/{recordId:long}/retry", async (
    Guid id, long recordId, AccountRuntimeManager manager) =>
{
    await manager.RetryOutboxAsync(id, recordId);
    return Results.Accepted();
});

app.Use(async (context, next) =>
{
    try { await next(); }
    catch (KeyNotFoundException ex) { await WriteProblem(context, 404, ex.Message); }
    catch (ArgumentException ex) { await WriteProblem(context, 400, ex.Message); }
    catch (TimeoutException ex) { await WriteProblem(context, 408, ex.Message); }
    catch (SocketException ex) { await WriteProblem(context, 503, $"代理端口连接失败：{ex.Message}"); }
    catch (InvalidOperationException ex) { await WriteProblem(context, 409, ex.Message); }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Unhandled API exception");
        await WriteProblem(context, 500, "服务器处理失败，请查看容器日志");
    }
});

app.MapFallbackToFile("index.html");
app.Run();

static bool FixedTimeEquals(string actualValue, string expectedValue)
{
    var actual = Encoding.UTF8.GetBytes(actualValue ?? string.Empty);
    var expected = Encoding.UTF8.GetBytes(expectedValue);
    return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
}

static async Task WriteProblem(HttpContext context, int status, string detail)
{
    if (context.Response.HasStarted) return;
    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(new { title = "操作失败", detail, status });
}

internal sealed record LoginInput(string Value);
internal sealed record AdminLoginInput(string Username, string Password, bool RememberMe);
internal sealed record RecordIdsInput(IReadOnlyList<long> Ids);
internal sealed record EmailSettingsInput(
    string SmtpHost, int SmtpPort, bool EnableSsl, string UserName,
    string Password, string FromAddress, bool ClearPassword);
internal sealed record EmailTestInput(string Recipient);
internal sealed record ProxyTestInput(string Host, int Port);

internal sealed record ScheduleInput(
    Guid? Id,
    bool Enabled,
    long ChatId,
    string ChatKind,
    string ChatTitle,
    string Time,
    SchedulePeriod Period,
    IReadOnlyList<DayOfWeek>? WeekDays,
    string Message)
{
    public ScheduledMessage ToModel()
    {
        if (!TimeSpan.TryParse(Time, out var time)) throw new ArgumentException("时间格式必须为 HH:mm");
        return new ScheduledMessage
        {
            Id = Id ?? Guid.NewGuid(),
            Enabled = Enabled,
            ChatId = ChatId,
            ChatKind = ChatKind,
            ChatTitle = ChatTitle,
            Time = time,
            Period = Period,
            WeekDays = WeekDays?.Distinct().ToList() ?? [],
            Message = Message
        };
    }
}

internal sealed record IntervalChatRuleInput(
    Guid? Id,
    bool Enabled,
    string Name,
    long SourceChatId,
    string SourceChatKind,
    string SourceChatTitle,
    long TargetChatId,
    string TargetChatKind,
    string TargetChatTitle,
    int IntervalMinutes,
    int MinimumMessageCount,
    int SummaryLineCount)
{
    public IntervalChatRule ToModel() => new()
    {
        Id = Id ?? Guid.NewGuid(),
        Enabled = Enabled,
        Name = Name,
        SourceChatId = SourceChatId,
        SourceChatKind = SourceChatKind,
        SourceChatTitle = SourceChatTitle,
        TargetChatId = TargetChatId,
        TargetChatKind = TargetChatKind,
        TargetChatTitle = TargetChatTitle,
        IntervalMinutes = IntervalMinutes,
        MinimumMessageCount = MinimumMessageCount,
        SummaryLineCount = SummaryLineCount,
        WindowStartedAt = DateTimeOffset.Now,
        LastCheckedAt = DateTimeOffset.Now
    };
}

internal sealed class AccountRuntimeHostedService(AccountRuntimeManager manager, ILogger<AccountRuntimeHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting configured Telegram accounts");
        await manager.StartAutoAccountsAsync(stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Stopping Telegram account runtimes");
        await base.StopAsync(cancellationToken);
        await manager.DisposeAsync();
    }
}

internal static class SystemStart
{
    public static readonly DateTimeOffset StartedAt = DateTimeOffset.Now;
}

public partial class Program { }
