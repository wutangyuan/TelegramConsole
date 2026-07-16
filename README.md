# TelegramConsole

基于 WTelegramClient、WPF、Quartz.NET 和 log4net 的 Telegram 桌面控制台助手。

## 解决方案结构

- `TelegramConsole.Core`：业务模型与服务接口。
- `TelegramConsole.Infrastructure`：Telegram、Quartz、SQLite、日志、加密存储、代理和邮件实现。
- `TelegramConsoleApp`：WPF 桌面界面。

## 主要功能

- Telegram 一键登录，支持验证码和两步验证密码。
- 登录成功后锁定登录入口，仅显示当前账号和在线/连接异常状态，避免误点重复登录。
- 私聊、群聊和独立 TUI 风格聊天控制台。
- 控制台可区分图片、视频、语音、贴纸和文件等媒体类型；可下载的媒体标签可点击并按账号缓存后打开。
- 群消息监控。
- “@我的消息”按账号保存提及记录，并可通知到指定机器人或私聊。
- 每日或每周定时签到，支持勾选、多选、编辑、立即执行，以及 Telegram/邮件完成通知。
- 可靠加密发件箱记录发送状态并阻止重复发送；结果未知的消息只允许人工确认后重试。
- Telegram 服务器一次性定时消息，程序关闭后仍由 Telegram 按时发送。
- 全局/会话消息搜索（Telegram 远端搜索 + 账号隔离的本地 SQLite FTS 索引），支持 Forum Topic 筛选。
- 搜索结果可直接回复、引用回复、转发、编辑、删除并复制频道消息链接。
- 可配置关键词、正则、@、会话或发送人自动化规则，执行 Telegram、邮件或日志动作。
- Telegram 云草稿和会话文件夹管理。
- log4net 运行日志和 SQLite 异常中心；只有真实异常日志入库，界面业务校验仅弹窗提示。
- 支持代理配置，SOCKS5 默认 `127.0.0.1:7890`，并对代理长连接启用 TCP keep-alive。
- 支持中文/英文界面；未设置语言偏好时跟随系统语言。
- Telegram session 按手机号隔离，任务和异常数据按 Telegram 用户 ID 隔离。
- 本地配置使用 Windows DPAPI 加密保存。
- 主窗口关闭时默认隐藏到系统托盘，定时任务和监控继续运行；托盘右键显示当前账号，并可恢复窗口或彻底退出。

## 构建

详细使用方法见 [效率工具使用说明](docs/EFFICIENCY_TOOLS.md)。

```powershell
dotnet build .\TelegramConsole.sln
```

发布文件统一输出到 `output` 目录，该目录不会提交到版本库。

## License

本项目使用 [MIT License](LICENSE)。
