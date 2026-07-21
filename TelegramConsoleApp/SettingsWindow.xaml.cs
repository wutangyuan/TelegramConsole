using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;

namespace TelegramConsoleApp;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ISettingsStore _store;

    public SettingsWindow(AppSettings settings, ISettingsStore store)
    {
        InitializeComponent();
        _settings = settings;
        _store = store;
        var globalSettings = store.Load();
        LanguageBox.SelectedIndex = globalSettings.Language.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) ? 1
            : globalSettings.Language.Equals("en-US", StringComparison.OrdinalIgnoreCase) ? 2
            : 0;
        EnabledBox.IsChecked = globalSettings.Proxy.Enabled;
        TypeBox.SelectedIndex = globalSettings.Proxy.Type.Equals("MtProxy", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        HostBox.Text = string.IsNullOrWhiteSpace(globalSettings.Proxy.Host) ? "127.0.0.1" : globalSettings.Proxy.Host;
        PortBox.Text = globalSettings.Proxy.Port is > 0 and <= 65535 ? globalSettings.Proxy.Port.ToString() : "7890";
        UserNameBox.Text = globalSettings.Proxy.UserName;
        PasswordBox.Password = globalSettings.Proxy.Password;
        MtProxyUrlBox.Text = globalSettings.Proxy.MtProxyUrl;
        SmtpHostBox.Text = globalSettings.Email.SmtpHost;
        SmtpPortBox.Text = globalSettings.Email.SmtpPort.ToString();
        SmtpUserBox.Text = globalSettings.Email.UserName;
        SmtpPasswordBox.Password = globalSettings.Email.Password;
        SmtpFromBox.Text = globalSettings.Email.FromAddress;
        SmtpSslBox.IsChecked = globalSettings.Email.EnableSsl;
        LoadAiSettings(globalSettings.AiAssistant);
        UpdateAvailability();
    }

    private string SelectedType => (TypeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Socks5";

    private void ProxyOption_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) UpdateAvailability();
    }

    private void UpdateAvailability()
    {
        var enabled = EnabledBox.IsChecked == true;
        var socks = SelectedType == "Socks5";
        TypeBox.IsEnabled = enabled;
        HostBox.IsEnabled = PortBox.IsEnabled = UserNameBox.IsEnabled = PasswordBox.IsEnabled = TestButton.IsEnabled = enabled && socks;
        MtProxyUrlBox.IsEnabled = enabled && !socks;
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        var originalContent = TestButton.Content;
        try
        {
            var (host, port) = ValidateSocks5();
            TestButton.IsEnabled = false;
            TestButton.Content = LocalizationManager.Text("Testing");
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            HintText.Text = LocalizationManager.Format("TestingProxy", host, port);
            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(host, port, timeout.Token);
            HintText.Text = LocalizationManager.Format("ProxyTestSuccess", host, port);
            System.Windows.MessageBox.Show(
                this,
                $"代理端口连接成功。\n\n地址：{host}\n端口：{port}\n\n这表示本机端口可访问，保存后点击“一键登录”可测试 Telegram 完整连接。",
                LocalizationManager.Text("ProxyTestSuccess").Split('：', ':')[0],
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            HintText.Text = LocalizationManager.Text("ProxyTimeout");
            System.Windows.MessageBox.Show(
                this,
                "连接代理端口超时（5 秒）。请确认代理软件正在运行，并检查 SOCKS5/Mixed 端口。",
                LocalizationManager.Text("ProxyTestFailed"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            var message = UserMessageFormatter.From(ex);
            HintText.Text = LocalizationManager.Format("ErrorPrefix", message);
            System.Windows.MessageBox.Show(
                this,
                message,
                LocalizationManager.Text("ProxyTestFailed"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
            TestButton.Content = originalContent;
            TestButton.IsEnabled = true;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var enabled = EnabledBox.IsChecked == true;
            var type = SelectedType;
            if (enabled && type == "Socks5") ValidateSocks5();
            if (enabled && type == "MtProxy" && !IsValidMtProxyUrl(MtProxyUrlBox.Text))
                throw new InvalidOperationException("请输入有效的 https://t.me/proxy?... MTProxy 链接");
            var email = ReadEmailSettings();

            _settings.Proxy.Enabled = enabled;
            _settings.Proxy.Type = type;
            _settings.Proxy.Host = HostBox.Text.Trim();
            _settings.Proxy.Port = int.TryParse(PortBox.Text.Trim(), out var port) ? port : 7890;
            _settings.Proxy.UserName = UserNameBox.Text.Trim();
            _settings.Proxy.Password = PasswordBox.Password;
            _settings.Proxy.MtProxyUrl = MtProxyUrlBox.Text.Trim();
            _settings.Email = email;
            _settings.AiAssistant = ReadAiSettings(_settings.AiAssistant);
            _settings.Language = (LanguageBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
            _store.SaveGlobalSettings(_settings);
            LocalizationManager.ApplyLanguage(_settings.Language);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, UserMessageFormatter.From(ex), LocalizationManager.Text("ProxySettingsTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void SmtpTestButton_Click(object sender, RoutedEventArgs e)
    {
        var originalContent = SmtpTestButton.Content;
        try
        {
            var email = ReadEmailSettings();
            var recipient = SmtpTestRecipientBox.Text.Trim();
            if (recipient.Length == 0) recipient = email.FromAddress;
            _ = new System.Net.Mail.MailAddress(recipient);

            SmtpTestButton.IsEnabled = false;
            SmtpTestButton.Content = LocalizationManager.Text("Testing");
            SmtpHintText.Text = LocalizationManager.Text("SendingTestEmail");
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;

            await EmailNotificationService.SendAsync(
                email,
                recipient,
                LocalizationManager.Text("TestEmailSubject"),
                LocalizationManager.Text("TestEmailBody"));

            SmtpHintText.Text = LocalizationManager.Format("TestEmailSuccessHint", recipient);
            System.Windows.MessageBox.Show(
                this,
                LocalizationManager.Format("TestEmailSuccess", recipient),
                LocalizationManager.Text("SendTestEmail"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            var message = UserMessageFormatter.From(ex);
            SmtpHintText.Text = LocalizationManager.Format("ErrorPrefix", message);
            System.Windows.MessageBox.Show(
                this,
                message,
                LocalizationManager.Text("TestEmailFailed"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
            SmtpTestButton.Content = originalContent;
            SmtpTestButton.IsEnabled = true;
        }
    }

    private void AiWizardButton_Click(object sender, RoutedEventArgs e)
    {
        var choice = System.Windows.MessageBox.Show(
            this,
            "选择“是”预填本地 Ollama（本机/NAS）；选择“否”预填 OpenAI 或其他兼容云服务。",
            "AI 配置向导", MessageBoxButton.YesNoCancel, MessageBoxImage.Information);
        if (choice == MessageBoxResult.Cancel) return;
        AiEnabledBox.IsChecked = true;
        if (choice == MessageBoxResult.Yes)
        {
            AiEndpointBox.Text = "http://127.0.0.1:11434/v1";
            AiModelBox.Text = "qwen2.5:7b";
            AiApiKeyBox.Password = "";
            AiStatusText.Text = "已预填本地 Ollama 示例。Docker/NAS 部署时请改为容器可访问的宿主机地址。";
        }
        else
        {
            AiEndpointBox.Text = "https://api.openai.com/v1";
            AiModelBox.Text = "gpt-4.1-mini";
            AiStatusText.Text = "已预填 OpenAI 示例。请填写 API Key，或换成兼容服务商提供的地址和模型。";
        }
        AiEndpointBox.Focus();
    }

    private void AiProviderBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateAiProviderUi();
    }

    private void AiCodexLoginButton_Click(object sender, RoutedEventArgs e)
    {
        var cli = CodexCliOAuthAssistantService.FindCodexCli();
        if (cli is null)
        {
            System.Windows.MessageBox.Show(this, "未找到 Codex CLI。请先安装 Codex 桌面端/CLI 后重试。", "未找到 Codex CLI", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                // A .cmd shim needs CALL. Keep the terminal open so the browser/device login text remains visible.
                Arguments = $"/k call \"{cli}\" login --device-auth",
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(start);
            AiStatusText.Text = "已打开 Codex 登录窗口。请完成浏览器授权，完成后返回本页点击“测试连接”。";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, UserMessageFormatter.From(ex), "无法启动 Codex 登录", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void AiTestButton_Click(object sender, RoutedEventArgs e)
    {
        var original = AiTestButton.Content;
        try
        {
            var settings = ReadAiSettings(_settings.AiAssistant);
            settings.Enabled = true;
            AiTestButton.IsEnabled = false;
            AiTestButton.Content = "测试中…";
            AiStatusText.Text = "正在请求 AI 服务…";
            var result = await new OpenAiCompatibleAssistantService().TestAsync(settings);
            AiStatusText.Text = $"连接成功：{result.Text}";
        }
        catch (Exception ex)
        {
            AiStatusText.Text = UserMessageFormatter.From(ex);
            System.Windows.MessageBox.Show(this, AiStatusText.Text, "AI 连接测试失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            AiTestButton.IsEnabled = true;
            AiTestButton.Content = original;
        }
    }

    private (string Host, int Port) ValidateSocks5()
    {
        var host = HostBox.Text.Trim();
        if (host.Length == 0) throw new InvalidOperationException(LocalizationManager.Text("ProxyAddressRequired"));
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
            throw new InvalidOperationException(LocalizationManager.Text("ProxyPortInvalid"));
        return (host, port);
    }

    private static bool IsValidMtProxyUrl(string value) =>
        Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
        && uri.Host.Equals("t.me", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.Equals("/proxy", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(uri.Query);

    private EmailSettings ReadEmailSettings()
    {
        var host = SmtpHostBox.Text.Trim();
        var userName = SmtpUserBox.Text.Trim();
        var password = SmtpPasswordBox.Password;
        var from = SmtpFromBox.Text.Trim();
        var hasAnyValue = host.Length > 0 || userName.Length > 0 || password.Length > 0 || from.Length > 0;
        if (!int.TryParse(SmtpPortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
            throw new InvalidOperationException(LocalizationManager.Text("SmtpPortInvalid"));
        if (hasAnyValue && (host.Length == 0 || from.Length == 0))
            throw new InvalidOperationException(LocalizationManager.Text("EmailConfigIncomplete"));
        return new EmailSettings
        {
            SmtpHost = host,
            SmtpPort = port,
            UserName = userName,
            Password = password,
            FromAddress = from,
            EnableSsl = SmtpSslBox.IsChecked == true
        };
    }

    private void LoadAiSettings(AiAssistantSettings settings)
    {
        AiEnabledBox.IsChecked = settings.Enabled;
        AiProviderBox.SelectedIndex = settings.UseCodexCliOAuth || string.Equals(settings.Provider, "CodexCliOAuth", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        AiEndpointBox.Text = settings.Endpoint;
        AiModelBox.Text = settings.Model;
        AiApiKeyBox.Password = "";
        AiContextLimitBox.Text = Math.Clamp(settings.ContextMessageLimit, 5, 100).ToString();
        AiStatusText.Text = string.IsNullOrWhiteSpace(settings.ApiKey)
            ? "未配置 API Key（本地 Ollama 通常可留空）"
            : "已保存加密 API Key；留空不会覆盖。";
        UpdateAiProviderUi();
    }

    private AiAssistantSettings ReadAiSettings(AiAssistantSettings previous)
    {
        if (!int.TryParse(AiContextLimitBox.Text, out var contextLimit)) contextLimit = 30;
        var enabled = AiEnabledBox.IsChecked == true;
        var useCodexCliOAuth = (AiProviderBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() == "CodexCliOAuth";
        var endpoint = AiEndpointBox.Text.Trim();
        var model = AiModelBox.Text.Trim();
        if (enabled && !useCodexCliOAuth && (endpoint.Length == 0 || model.Length == 0))
            throw new InvalidOperationException("启用 AI 前，请填写接口地址和模型名称");
        return new AiAssistantSettings
        {
            Enabled = enabled,
            Provider = useCodexCliOAuth ? "CodexCliOAuth" : "OpenAICompatible",
            UseCodexCliOAuth = useCodexCliOAuth,
            Endpoint = endpoint,
            Model = model,
            ApiKey = string.IsNullOrWhiteSpace(AiApiKeyBox.Password) ? previous.ApiKey : AiApiKeyBox.Password,
            ContextMessageLimit = Math.Clamp(contextLimit, 5, 100),
            // Codex CLI has process startup plus subscription-side routing overhead; keep the
            // API-compatible timeout for that provider, but give OAuth calls a practical floor.
            TimeoutSeconds = useCodexCliOAuth ? Math.Max(previous.TimeoutSeconds, 180) : previous.TimeoutSeconds
        };
    }

    private void UpdateAiProviderUi()
    {
        if (AiProviderBox is null) return;
        var codex = (AiProviderBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() == "CodexCliOAuth";
        AiEndpointBox.IsEnabled = !codex;
        AiApiKeyBox.IsEnabled = !codex;
        AiCodexLoginButton.Visibility = codex ? Visibility.Visible : Visibility.Collapsed;
        AiApiKeyLabel.Text = codex ? "本机登录" : "API Key";
        AiHintText.Text = codex
            ? "此方式复用本机 Codex CLI 的 ChatGPT/Codex 登录；本应用不会读取或保存 OAuth Token。点击“登录 ChatGPT/Codex”完成设备授权后测试。仅支持运行了 Codex CLI 的桌面主机。"
            : "向导可预填本地 Ollama 或 OpenAI 兼容服务。保存后，各账户的会话摘要和 AI 回复草稿都会使用此连接；AI 不会自动发送 Telegram 消息。";
    }
}
