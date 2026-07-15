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
        EnabledBox.IsChecked = settings.Proxy.Enabled;
        TypeBox.SelectedIndex = settings.Proxy.Type.Equals("MtProxy", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        HostBox.Text = string.IsNullOrWhiteSpace(settings.Proxy.Host) ? "127.0.0.1" : settings.Proxy.Host;
        PortBox.Text = settings.Proxy.Port is > 0 and <= 65535 ? settings.Proxy.Port.ToString() : "7890";
        UserNameBox.Text = settings.Proxy.UserName;
        PasswordBox.Password = settings.Proxy.Password;
        MtProxyUrlBox.Text = settings.Proxy.MtProxyUrl;
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
            TestButton.Content = "测试中...";
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            HintText.Text = $"正在连接 {host}:{port}...";
            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(host, port, timeout.Token);
            HintText.Text = $"测试成功：本机代理端口 {host}:{port} 可以连接。";
            System.Windows.MessageBox.Show(
                this,
                $"代理端口连接成功。\n\n地址：{host}\n端口：{port}\n\n这表示本机端口可访问，保存后点击“一键登录”可测试 Telegram 完整连接。",
                "代理测试成功",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            HintText.Text = "测试失败：连接代理端口超时（5 秒）。";
            System.Windows.MessageBox.Show(
                this,
                "连接代理端口超时（5 秒）。请确认代理软件正在运行，并检查 SOCKS5/Mixed 端口。",
                "代理测试失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            HintText.Text = "测试失败：" + ex.Message;
            System.Windows.MessageBox.Show(
                this,
                $"无法连接代理端口。\n\n{ex.Message}",
                "代理测试失败",
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

            _settings.Proxy.Enabled = enabled;
            _settings.Proxy.Type = type;
            _settings.Proxy.Host = HostBox.Text.Trim();
            _settings.Proxy.Port = int.TryParse(PortBox.Text.Trim(), out var port) ? port : 7890;
            _settings.Proxy.UserName = UserNameBox.Text.Trim();
            _settings.Proxy.Password = PasswordBox.Password;
            _settings.Proxy.MtProxyUrl = MtProxyUrlBox.Text.Trim();
            _store.Save(_settings);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "代理设置", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private (string Host, int Port) ValidateSocks5()
    {
        var host = HostBox.Text.Trim();
        if (host.Length == 0) throw new InvalidOperationException("代理地址不能为空");
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
            throw new InvalidOperationException("代理端口必须是 1 到 65535 之间的整数");
        return (host, port);
    }

    private static bool IsValidMtProxyUrl(string value) =>
        Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
        && uri.Host.Equals("t.me", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.Equals("/proxy", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(uri.Query);
}
