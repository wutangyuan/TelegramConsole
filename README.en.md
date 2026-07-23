# TelegramConsole

[中文](README.md) | [English](README.en.md)

A Telegram desktop and NAS console assistant built with WTelegramClient, WPF, Quartz.NET, log4net, and Microsoft Agent Framework (MAF).

## Solution layout

- `TelegramConsole.Core`: business models and service contracts.
- `TelegramConsole.Infrastructure`: Telegram, Quartz, SQLite, logging, encrypted storage, proxy, and mail implementations.
- `TelegramConsole.Runtime`: reusable long-running multi-account runtime shared by desktop and web.
- `TelegramConsole.AI`: standalone AI library that centralizes Microsoft Agent Framework (MAF), OpenAI-compatible providers, and local Codex CLI sign-in integration.
- `TelegramConsole.Web`: NAS/Docker-oriented web management site and API.
- `TelegramConsoleApp`: WPF desktop client.

## Highlights

- One-click Telegram login with verification code and two-step password support.
- A single management center for accounts, device resources, exceptions, and runtime logs; account workspaces focus on chat, monitoring, and account automation.
- Private chats, groups, and an independent terminal-style chat console.
- Console, visual, and split chat views. The visual view uses the same history and live message stream as the console.
- Rich visual messages: replies, edits, recalls, media, reactions, forwarded source, signatures, links, and downloadable media.
- Group monitoring, per-account mention records, Telegram/private-message notifications, and configurable daily/weekly sign-in tasks.
- Interval conversation analysis: waits until a configurable message minimum is reached, generates a digest, and sends it to a target chat.
- Reliable encrypted outbox with duplicate-send prevention and manual confirmation for unknown results.
- Message search, reply/quote/forward/edit/delete actions, server scheduled messages, cloud drafts, and chat folders.
- Configurable keyword, regular-expression, mention, chat, sender, and automation rules.
- log4net runtime logs and a SQLite exception center. Only real exceptions are stored.
- SOCKS5 proxy support (default `127.0.0.1:7890`) with TCP keep-alive for proxy connections.
- Chinese and English UI. If no language preference is set, the system language is used.
- Session, task, exception, and media data are isolated per Telegram account. Local settings are protected with Windows DPAPI.
- AI is configured globally and enabled per account. OpenAI, DeepSeek, Ollama, and other OpenAI-compatible services are executed through the Microsoft Agent Framework (MAF) agent pipeline for summaries, reply drafts, interval analysis, and configurable group-member auto replies.
- The NAS web console provides the same resource metrics and supports stable incremental refresh of the latest 300 messages, quote replies, edits, recalls, and reactions.

## Build

For the Chinese efficiency-tools guide, see [docs/EFFICIENCY_TOOLS.md](docs/EFFICIENCY_TOOLS.md).

```powershell
dotnet build .\TelegramConsole.sln
```

## NAS / Docker

The NAS edition uses an independent data directory and can run as a long-lived background service:

```powershell
Copy-Item .env.example .env
docker compose up -d --build
```

The default URL is `http://localhost:5080`. See the Chinese [NAS Docker deployment guide](docs/NAS_DOCKER.md) for proxy and backup configuration.

Build artifacts are placed under `output`, which is excluded from version control.

## License

This project is licensed under the [GNU General Public License v3.0](LICENSE). Derivative distributions must comply with GPL-3.0 open-source and distribution requirements.
