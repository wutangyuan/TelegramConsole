# TelegramConsole

基于 WTelegramClient、WPF、Quartz.NET 和 log4net 的 Telegram 桌面控制台助手。

## 解决方案结构

- `TelegramConsole.Core`：业务模型与服务接口。
- `TelegramConsole.Infrastructure`：Telegram、Quartz、SQLite、日志、加密存储、代理和邮件实现。
- `TelegramConsoleApp`：WPF 桌面界面。

## 主要功能

- Telegram 一键登录，支持验证码和两步验证密码。
- 私聊、群聊和独立 TUI 风格聊天控制台。
- 群消息监控。
- “@我的消息”按账号保存提及记录，并可通知到指定机器人或私聊。
- 每日或每周定时签到，以及 Telegram/邮件完成通知。
- log4net 运行日志和 SQLite 异常中心。
- Telegram session 按手机号隔离，任务和异常数据按 Telegram 用户 ID 隔离。
- 本地配置使用 Windows DPAPI 加密保存。
- 主窗口关闭时默认隐藏到系统托盘，定时任务和监控继续运行；托盘菜单可恢复窗口或彻底退出。

## 构建

```powershell
dotnet build .\TelegramConsole.sln
```

发布文件统一输出到 `output` 目录，该目录不会提交到版本库。

## License

本项目使用 [MIT License](LICENSE)。
