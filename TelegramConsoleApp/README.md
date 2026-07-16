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
- 登录成功后隐藏 API 参数输入区，只显示当前账号和在线状态；连接断开时恢复登录配置区并提示重新登录。
- 左侧选择私聊、群聊或频道，在“聊天终端”查看最近消息并发送文本。
- 图片、视频、语音、贴纸及文件显示具体媒体标签；可下载媒体的标签为链接，点击后按账号缓存并使用系统默认程序打开。
- 双击会话、右键会话或点击“独立控制台”，可为会话打开单独的深色聊天窗口。
- 左侧会话列表支持展开、收起和空屏模式，便于只保留聊天终端区域。
- “群消息监控”实时显示所有群聊/频道的新消息。
- 定时任务由 Quartz.NET 3.18.2 调度；每条任务使用独立 Cron 触发器，支持每日/每周、勾选、多选、编辑、删除、立即执行、错过执行后补发，并防止并发和当天重复发送。
- “效率工具”支持 Telegram 服务器托管的一次性定时消息，可查询和删除尚未发送的计划消息；这类任务不要求应用保持运行。
- 所有手动、签到、确认和规则通知均经过可靠发件箱：正文使用 DPAPI 加密，发送成功记录 Telegram 消息 ID，重复操作会被拦截；网络中断导致结果未知时不会自动重发。
- “消息搜索与操作”同时查询 Telegram 和本地 SQLite 索引；索引按账号隔离，并兼顾 FTS 与中文子串搜索。支持 Forum Topic、回复、引用回复、转发、编辑本人消息、删除和频道消息链接。
- 自动化规则支持关键词、正则表达式、@我的消息、指定会话和发送人触发，可发送 Telegram 通知、SMTP 邮件或仅写日志；规则按账号加密保存。
- 支持读取、保存和清除 Telegram 云草稿，以及查看和创建 Telegram 会话文件夹。
- 签到成功后可向另一个 Telegram 用户/群聊发送完成确认，也可通过 SMTP 发送确认邮件。
- SMTP 配置统一放在“设置”窗口；任务、异常通知或 @ 通知选择邮件目标时会校验邮箱配置。
- API 参数、手机号、SMTP 密码和任务配置使用 Windows DPAPI（当前用户范围）加密保存。
- Telegram session 按手机号隔离；定时任务、异常记录和异常通知配置按登录成功后的 Telegram 用户 ID 隔离，切换账号不会显示或执行其他账号的数据。
- “设置”窗口支持语言偏好、关闭代理、SOCKS5 和 Telegram MTProxy；SOCKS5 默认地址为 `127.0.0.1:7890`。
- 默认支持中文和英文界面；未设置语言偏好时跟随系统语言，中文系统使用 `zh-CN`，其他语言使用 `en-US`。
- SOCKS5 代理连接启用 TCP keep-alive；WTelegramClient keep-alive 心跳间隔设置为 30 秒，自动重连次数保持 WTelegramClient 默认值。
- “运行日志”页实时显示应用、Telegram 和 Quartz 日志；底层使用 log4net 3.3.2，文件按天保存在 `%LOCALAPPDATA%\TelegramConsoleApp\logs`，保留 30 天并自动脱敏。
- “异常中心”仅将带 Exception/堆栈的 Error/Critical 日志写入 `%LOCALAPPDATA%\TelegramConsoleApp\exceptions.db`；界面主动抛出的业务提示不会入库。支持按日期、级别和关键字查询，可将异常通知发送到 Telegram 用户/群聊或 SMTP 邮箱，并支持多选重发。
- Telegram 主连接断开会在界面提示并记录异常日志，底部状态栏只显示简洁中文提示，原始 WTelegram 诊断信息保留在日志/异常详情里。
- 主窗口关闭时默认隐藏到系统托盘，定时任务和消息监控继续运行；托盘菜单支持恢复窗口和彻底退出。

## 运行

效率工具的字段说明、操作步骤和自动化规则示例见 [效率工具使用说明](../docs/EFFICIENCY_TOOLS.md)。

```powershell
dotnet run --project .\TelegramConsoleApp\TelegramConsoleApp.csproj
```

所有 .NET 构建中间文件和发布文件统一输出到工作区的 `output` 目录。发布命令：

```powershell
dotnet publish .\TelegramConsoleApp\TelegramConsoleApp.csproj -c Release -r win-x64 --self-contained false
```

Telegram API 参数需在 <https://my.telegram.org/apps> 申请。配置和登录会话保存在：

`%LOCALAPPDATA%\TelegramConsoleApp`

加密配置文件为 `settings.dat`，只能由保存它的 Windows 用户解密。里面保存 API 参数、手机号、代理、邮箱、语言偏好、账号级任务和通知偏好等应用配置。旧版的 `settings.json` 会在首次启动时自动迁移并删除。邮箱密码建议填写邮件服务商提供的 SMTP 授权码，不要填写网页登录密码。

可靠发件箱保存在 `outbox.db`，消息正文列使用 Windows DPAPI 加密；本地搜索索引保存在 `messages.db`，只保存已加载或运行期间收到的消息，查询始终按 Telegram 用户 ID 隔离。

定时任务由应用进程执行，因此到点时应用需要保持运行并处于登录状态。
