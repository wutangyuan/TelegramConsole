# Telegram 控制台助手

基于 NuGet 官方包 `WTelegramClient 4.4.7` 构建的 WPF Windows 桌面应用。

## 项目结构

使用根目录的 `TelegramConsole.sln` 可一次打开和构建全部项目。

- `TelegramConsole.Core`：纯业务模型与服务接口，不依赖 UI、WTelegramClient 或 Quartz。
- `TelegramConsole.Infrastructure`：WTelegramClient、Quartz、log4net、DPAPI、SMTP 和代理实现。
- `TelegramConsoleApp`：WPF 展示层，仅通过 Core 接口调用基础设施服务。

未来增加 ASP.NET Core 网页项目时，可直接引用 `TelegramConsole.Core`，并按部署环境复用或替换 Infrastructure 实现。

## 功能

- 配置 `api_id`、`api_hash`、手机号后点击“一键登录”；首次登录按提示输入验证码/两步验证密码，后续复用本地会话。
- 左侧选择私聊、群聊或频道，在“聊天终端”查看最近消息并发送文本。
- 双击会话、右键会话或点击“独立控制台”，可为会话打开单独的深色聊天窗口。
- “群消息监控”实时显示所有群聊/频道的新消息。
- 定时任务由 Quartz.NET 3.18.2 调度；每条任务使用独立的每日 Cron 触发器，支持错过执行后补发并防止并发和当天重复发送。
- 签到成功后可向另一个 Telegram 用户/群聊发送完成确认，也可通过 SMTP 发送确认邮件。
- API 参数、手机号、SMTP 密码和任务配置使用 Windows DPAPI（当前用户范围）加密保存。
- Telegram session 按手机号隔离；定时任务、异常记录和异常通知配置按登录成功后的 Telegram 用户 ID 隔离，切换账号不会显示或执行其他账号的数据。
- “设置”窗口支持关闭代理、SOCKS5 和 Telegram MTProxy；SOCKS5 默认地址为 `127.0.0.1:7890`。
- “运行日志”页实时显示应用、Telegram 和 Quartz 日志；底层使用 log4net 3.3.2，文件按天保存在 `%LOCALAPPDATA%\TelegramConsoleApp\logs`，保留 30 天并自动脱敏。
- “异常中心”仅将带 Exception/堆栈的 Error/Critical 日志写入 `%LOCALAPPDATA%\TelegramConsoleApp\exceptions.db`；支持按日期、级别和关键字查询，可将异常通知发送到 Telegram 用户/群聊或 SMTP 邮箱，并支持多选重发。

## 运行

```powershell
dotnet run --project .\TelegramConsoleApp\TelegramConsoleApp.csproj
```

所有 .NET 构建中间文件和发布文件统一输出到工作区的 `output` 目录。发布命令：

```powershell
dotnet publish .\TelegramConsoleApp\TelegramConsoleApp.csproj -c Release -r win-x64 --self-contained false
```

Telegram API 参数需在 <https://my.telegram.org/apps> 申请。配置和登录会话保存在：

`%LOCALAPPDATA%\TelegramConsoleApp`

加密配置文件为 `settings.dat`，只能由保存它的 Windows 用户解密。旧版的
`settings.json` 会在首次启动时自动迁移并删除。邮箱密码建议填写邮件服务商提供的
SMTP 授权码，不要填写网页登录密码。

定时任务由应用进程执行，因此到点时应用需要保持运行并处于登录状态。
