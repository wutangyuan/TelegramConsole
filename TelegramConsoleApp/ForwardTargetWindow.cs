using System.Windows;
using System.Windows.Controls;
using TelegramConsole.Core;
using ComboBox = System.Windows.Controls.ComboBox;

namespace TelegramConsoleApp;

public sealed class ForwardTargetWindow : Window
{
    private readonly ComboBox _targetBox;
    public DialogItem? SelectedDialog => _targetBox.SelectedItem as DialogItem;

    public ForwardTargetWindow(IEnumerable<DialogItem> dialogs)
    {
        Title = Application.Current.TryFindResource("ForwardMessage") as string ?? "转发消息";
        Width = 480;
        Height = 190;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = System.Windows.Media.Brushes.White;
        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
        root.Children.Add(new TextBlock
        {
            Text = Application.Current.TryFindResource("SelectForwardTargetHint") as string ?? "选择转发目标",
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = System.Windows.Media.Brushes.SlateGray
        });
        _targetBox = new ComboBox { ItemsSource = dialogs.ToList(), DisplayMemberPath = "Name" };
        SearchableComboBoxBehavior.SetIsEnabled(_targetBox, true);
        Grid.SetRow(_targetBox, 1);
        root.Children.Add(_targetBox);

        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var cancel = new System.Windows.Controls.Button { Content = Application.Current.TryFindResource("Cancel") as string ?? "取消", Width = 84, Margin = new Thickness(0, 10, 8, 0) };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var confirm = new System.Windows.Controls.Button { Content = Application.Current.TryFindResource("ForwardMessage") as string ?? "转发", Width = 84, Margin = new Thickness(0, 10, 0, 0) };
        confirm.Click += (_, _) =>
        {
            if (_targetBox.SelectedItem is null) return;
            DialogResult = true;
            Close();
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        Content = root;
    }
}
