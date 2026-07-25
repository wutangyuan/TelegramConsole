param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'screenshots')
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$screens = @(
    @{ File = '01-chat-terminal'; Title = '聊天终端'; Subtitle = '控制台 / 可视化 / 分屏'; Kind = 'console' },
    @{ File = '02-group-monitor'; Title = '群消息监控'; Subtitle = '实时群消息流'; Kind = 'console' },
    @{ File = '03-schedules'; Title = '定时签到'; Subtitle = '每日、每周与通知'; Kind = 'table' },
    @{ File = '04-interval-analysis'; Title = '间隔分析'; Subtitle = '累计消息后生成简报'; Kind = 'table' },
    @{ File = '05-mentions'; Title = '@ 我的消息'; Subtitle = '提及记录与通知'; Kind = 'table' },
    @{ File = '06-exception-center'; Title = '异常中心'; Subtitle = '异常查询与通知配置'; Kind = 'table' },
    @{ File = '07-outbox'; Title = '发件箱'; Subtitle = '可靠发送与重试'; Kind = 'table' },
    @{ File = '08-ai-assistant'; Title = 'AI 助手'; Subtitle = '摘要、草稿与自动回复规则'; Kind = 'table' },
    @{ File = '09-runtime-logs'; Title = '运行日志'; Subtitle = '应用与连接诊断'; Kind = 'console' },
    @{ File = '10-productivity-search'; Title = '消息搜索与操作'; Subtitle = '本地与远端搜索'; Kind = 'table' },
    @{ File = '11-server-schedules'; Title = '服务器定时'; Subtitle = 'Telegram 托管的一次性消息'; Kind = 'table' },
    @{ File = '12-automation-rules'; Title = '自动化规则'; Subtitle = '关键词、正则和动作'; Kind = 'table' },
    @{ File = '13-drafts-folders'; Title = '草稿与文件夹'; Subtitle = '云草稿与会话分组'; Kind = 'table' },
    @{ File = '14-settings-proxy'; Title = '设置：连接代理'; Subtitle = 'SOCKS5 / MTProxy 连通性配置'; Kind = 'settings' },
    @{ File = '15-settings-smtp'; Title = '设置：邮件 SMTP'; Subtitle = '全局邮件通知配置'; Kind = 'settings' },
    @{ File = '16-settings-ai'; Title = '设置：AI 助手'; Subtitle = 'MAF 与 OpenAI 兼容服务'; Kind = 'settings' },
    @{ File = '17-management-accounts'; Title = '管理中心：账户管理'; Subtitle = '多账户工作区与 AI 开关'; Kind = 'table' },
    @{ File = '18-management-resources'; Title = '管理中心：设备资源'; Subtitle = '内存、磁盘和 Telegram 流量'; Kind = 'cards' },
    @{ File = '19-management-exceptions'; Title = '管理中心：异常中心'; Subtitle = '跨账户异常汇总'; Kind = 'table' },
    @{ File = '20-management-logs'; Title = '管理中心：运行日志'; Subtitle = '跨账户运行诊断'; Kind = 'console' }
)

function SvgText([string]$Text) { [System.Security.SecurityElement]::Escape($Text) }

foreach ($screen in $screens) {
    $title = SvgText $screen.Title
    $subtitle = SvgText $screen.Subtitle
    $body = switch ($screen.Kind) {
        'console' { @'
  <rect x="42" y="158" width="1116" height="456" rx="10" fill="#07131e"/>
  <text x="68" y="204" class="console green">[10:25:16] [示例工作群] demo_user：今天的任务已完成</text>
  <text x="68" y="242" class="console white">[10:25:32] [示例工作群] assistant：收到，已记录。</text>
  <text x="68" y="280" class="console blue">[10:26:08] [示例工作群] @example_account 请确认</text>
  <text x="68" y="318" class="console green">[10:26:15] 我：示例回复内容</text>
  <rect x="42" y="632" width="1116" height="54" rx="8" fill="#ffffff" stroke="#c9d8e7"/>
  <text x="68" y="666" class="muted">输入消息；Enter 发送，Alt+Enter 换行</text>
'@ }
        'settings' { @'
  <rect x="42" y="158" width="1116" height="528" rx="10" fill="#ffffff" stroke="#c9d8e7"/>
  <text x="76" y="214" class="label">启用</text><rect x="238" y="184" width="700" height="44" rx="6" fill="#f8fbff" stroke="#c9d8e7"/>
  <text x="76" y="280" class="label">服务地址</text><rect x="238" y="250" width="820" height="44" rx="6" fill="#f8fbff" stroke="#c9d8e7"/>
  <text x="76" y="346" class="label">模型 / 配置</text><rect x="238" y="316" width="620" height="44" rx="6" fill="#f8fbff" stroke="#c9d8e7"/>
  <text x="76" y="412" class="label">安全凭据</text><rect x="238" y="382" width="820" height="44" rx="6" fill="#f8fbff" stroke="#c9d8e7"/>
  <rect x="238" y="474" width="160" height="48" rx="8" fill="#2387d1"/><text x="276" y="505" class="button">保存配置</text>
  <text x="76" y="580" class="hint">所有内容均为脱敏演示数据；真实密钥不会出现在文档截图中。</text>
'@ }
        'cards' { @'
  <rect x="42" y="158" width="258" height="138" rx="10" fill="#ffffff" stroke="#c9d8e7"/><text x="66" y="204" class="label">应用内存</text><text x="66" y="258" class="metric">128 MB</text>
  <rect x="322" y="158" width="258" height="138" rx="10" fill="#ffffff" stroke="#c9d8e7"/><text x="346" y="204" class="label">磁盘 I/O</text><text x="346" y="258" class="metric">示例数据</text>
  <rect x="602" y="158" width="258" height="138" rx="10" fill="#ffffff" stroke="#c9d8e7"/><text x="626" y="204" class="label">Telegram 流量</text><text x="626" y="258" class="metric">示例数据</text>
  <rect x="882" y="158" width="276" height="138" rx="10" fill="#ffffff" stroke="#c9d8e7"/><text x="906" y="204" class="label">运行状态</text><text x="906" y="258" class="metric">正常</text>
  <rect x="42" y="324" width="1116" height="362" rx="10" fill="#ffffff" stroke="#c9d8e7"/>
  <polyline points="76,620 210,570 340,600 488,470 626,520 760,420 920,470 1120,370" fill="none" stroke="#2387d1" stroke-width="6"/>
'@ }
        default { @'
  <rect x="42" y="158" width="1116" height="78" rx="10" fill="#ffffff" stroke="#c9d8e7"/>
  <rect x="68" y="178" width="208" height="38" rx="6" fill="#f8fbff" stroke="#c9d8e7"/><rect x="294" y="178" width="160" height="38" rx="6" fill="#2387d1"/>
  <text x="320" y="203" class="button">新增 / 查询</text>
  <rect x="42" y="256" width="1116" height="430" rx="10" fill="#ffffff" stroke="#c9d8e7"/>
  <rect x="42" y="256" width="1116" height="48" rx="10" fill="#edf4fa"/>
  <text x="72" y="286" class="label">状态</text><text x="240" y="286" class="label">会话 / 规则</text><text x="520" y="286" class="label">配置 / 内容</text><text x="894" y="286" class="label">最近状态</text>
  <line x1="42" y1="358" x2="1158" y2="358" class="line"/><line x1="42" y1="426" x2="1158" y2="426" class="line"/><line x1="42" y1="494" x2="1158" y2="494" class="line"/><line x1="42" y1="562" x2="1158" y2="562" class="line"/>
  <text x="72" y="338" class="value">启用</text><text x="240" y="338" class="value">示例工作群</text><text x="520" y="338" class="value">脱敏演示内容</text><text x="894" y="338" class="value">已完成</text>
  <text x="72" y="406" class="value">启用</text><text x="240" y="406" class="value">Demo Assistant</text><text x="520" y="406" class="value">示例规则 / 消息</text><text x="894" y="406" class="value">等待执行</text>
'@ }
    }

    $svg = @"
<svg xmlns="http://www.w3.org/2000/svg" width="1200" height="720" viewBox="0 0 1200 720">
<style>
  .title { font: 700 27px 'Segoe UI', Arial; fill: #10243e; }
  .subtitle { font: 16px 'Segoe UI', Arial; fill: #58708b; }
  .label { font: 600 16px 'Segoe UI', Arial; fill: #1769aa; }
  .value { font: 16px 'Segoe UI', Arial; fill: #15314e; }
  .button { font: 600 15px 'Segoe UI', Arial; fill: #fff; }
  .muted, .hint { font: 14px 'Segoe UI', Arial; fill: #6d8197; }
  .metric { font: 700 26px 'Segoe UI', Arial; fill: #10243e; }
  .console { font: 600 18px Consolas, monospace; } .green { fill: #35d26b; } .white { fill: #f2f6fb; } .blue { fill: #39a4ff; }
  .line { stroke: #dce7f0; stroke-width: 1; }
</style>
<rect width="1200" height="720" fill="#f2f7fb"/>
<rect x="0" y="0" width="1200" height="82" fill="#ffffff"/>
<circle cx="46" cy="41" r="20" fill="#2387d1"/><text x="39" y="48" class="button">T</text>
<text x="82" y="39" class="title">TelegramConsole</text><text x="82" y="62" class="subtitle">Sanitized documentation demo · no real account or message data</text>
<rect x="0" y="82" width="1200" height="52" fill="#e7f0f8"/>
<text x="42" y="115" class="label">$title</text><text x="240" y="115" class="subtitle">$subtitle</text>
$body
<text x="42" y="708" class="muted">脱敏演示图：所有账号、会话、消息、地址和凭据均为虚构示例。</text>
</svg>
"@
    Set-Content -LiteralPath (Join-Path $OutputDirectory ($screen.File + '.svg')) -Value $svg -Encoding utf8
}

Write-Host "Generated $($screens.Count) sanitized documentation screenshots in $OutputDirectory"
