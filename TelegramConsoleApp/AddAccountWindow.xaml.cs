using System.Windows;

namespace TelegramConsoleApp;

public partial class AddAccountWindow : Window
{
    internal AccountLaunchRequest? Request { get; private set; }

    public AddAccountWindow() => InitializeComponent();

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var localName = LocalNameBox.Text.Trim();
        var phone = PhoneBox.Text.Trim();
        if (phone.Count(char.IsDigit) < 6)
        {
            System.Windows.MessageBox.Show(this, "请输入包含国家区号的有效手机号。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Request = new AccountLaunchRequest(localName, phone, AutoLogin: true);
        DialogResult = true;
    }
}
