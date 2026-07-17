# NAS Docker 部署

NAS 版本由 `TelegramConsole.Web` 和 `TelegramConsole.Runtime` 组成。它独立于 WPF 运行，不读取 Windows DPAPI 配置，也不会占用 WPF 当前使用的 Session。

## 本机启动

1. 复制环境变量模板并修改管理密码：

   ```powershell
   Copy-Item .env.example .env
   notepad .env
   ```

2. 构建并后台运行：

   ```powershell
   docker compose up -d --build
   ```

3. 打开 `http://localhost:5080`，浏览器弹出认证框时使用：

   - 打开页面后使用项目内置登录界面
   - 默认用户名：`admin`
   - 默认密码：`adminadmin`
   - 密码由 `.env` 中的 `TELEGRAMCONSOLE_ADMIN_PASSWORD` 控制，部署到公网前请修改

4. 查看状态和日志：

   ```powershell
   docker compose ps
   docker compose logs -f telegram-console
   ```

   进程健康检查为 `/health/live`，账户汇总状态为 `/health/ready`。单个 Telegram 账户断线不会让整个容器反复重启，可在管理首页单独查看和恢复该账户。

停止服务使用 `docker compose down`。不要添加 `-v`，否则会同时删除数据卷。

## NAS 部署

将仓库复制或拉取到支持 Docker Compose 的 NAS，创建 `.env` 后执行：

```bash
docker compose up -d --build
```

默认端口是 `5080`。建议只在局域网开放；远程访问优先使用 VPN。必须公网访问时，应在 NAS 反向代理中配置 HTTPS，并限制来源。

## 数据与备份

持久数据保存在 Docker 卷 `telegramconsole-data`：

- `accounts.dat`：AES-GCM 加密的账户连接配置。
- `settings.dat`：AES-GCM 加密的任务和通知配置。
- `master.key`：解密上述配置的主密钥。
- `sessions/`：WTelegram 独立账户 Session。
- `logs/`：按账户隔离的 log4net 日志。
- SQLite 数据库：异常、@消息、发件箱和消息索引。

备份时必须同时保存整个数据卷。只有 `accounts.dat` 而没有 `master.key` 无法恢复。可使用 NAS 自带的 Docker 卷备份功能，或停止容器后归档整个卷。

## 代理

容器中的 `127.0.0.1` 是容器自身，不是 NAS。新增账户时推荐：

- Docker Desktop：`host.docker.internal:7890`
- Linux NAS：Compose 已配置 `host-gateway`，通常也可使用 `host.docker.internal:7890`
- 如果 NAS 的 Docker 版本不支持 `host-gateway`，填写 NAS 的局域网地址，例如 `192.168.1.20:7890`

代理程序必须允许来自 Docker 网桥的连接，不能只监听宿主机回环地址。

## 更新

```bash
git pull
docker compose up -d --build
docker image prune -f
```

升级不会删除数据卷。正式升级前仍建议备份 `telegramconsole-data`。

## 当前 Web 功能

- 多账户添加、同时运行、停止和移除。
- Telegram 验证码、二次验证密码及其他登录步骤。
- 账户在线、恢复中、异常等运行状态。
- 最近 300 条实时消息。
- 会话列表和消息发送。
- 群消息实时监控，可按群组或频道筛选。
- 每日/每周定时任务、立即执行和删除。
- 间隔聊天分析：配置来源、目标、间隔分钟、最低消息数和摘要行数；不足阈值时保留累计窗口，成功发送后重置。
- `@我的消息`记录查询，按 Telegram 账户隔离。
- SQLite 异常中心：日期、级别和关键词筛选，详情/堆栈查看及通知重试。
- 发件箱查询和失败重试；结果未知的记录重试前会提示重复发送风险。
- 按账户查询运行日志，支持级别、关键词和异常详情筛选。
- 系统设置中统一保存 SMTP 服务器、授权码和发件地址，支持发送测试邮件；授权码随 `settings.dat` 加密保存且不会通过查询接口回显。
- 添加账户时可从 Docker 容器内部测试代理地址和端口连通性，便于确认 NAS 网络配置。
- Docker 自动重启、健康检查、优雅停止和持久化。

WPF 仍可按原方式独立运行。NAS 与 WPF 使用不同 Session，添加 NAS 账户不会终止当前 WPF 进程。
