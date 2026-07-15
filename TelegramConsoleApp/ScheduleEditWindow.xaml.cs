using System.Globalization;
using System.Windows;

namespace TelegramConsoleApp;

public partial class ScheduleEditWindow : Window
{
    private readonly ScheduledMessage _task;
    private readonly bool _emailConfigured;

    public ScheduleEditWindow(ScheduledMessage task, IReadOnlyCollection<DialogItem> dialogs, bool emailConfigured)
    {
        InitializeComponent();
        _task = task;
        _emailConfigured = emailConfigured;

        var targets = dialogs.Where(x => x.IsGroup)
            .Select(x => new PeerOption(x.Id, x.Kind, x.Name))
            .ToList();
        EnsureOption(targets, task.ChatId, task.ChatKind, task.ChatTitle);
        TargetBox.ItemsSource = targets;
        TargetBox.SelectedItem = targets.FirstOrDefault(x => x.Id == task.ChatId && x.Kind == task.ChatKind);

        var confirmations = new List<PeerOption> { new(null, "", LocalizationManager.Text("NoTelegramConfirmation")) };
        confirmations.AddRange(dialogs.Select(x => new PeerOption(x.Id, x.Kind, x.Name)));
        if (task.ConfirmationPeerId is long confirmationId)
            EnsureOption(confirmations, confirmationId, task.ConfirmationPeerKind, task.ConfirmationPeerTitle);
        ConfirmationPeerBox.ItemsSource = confirmations;
        ConfirmationPeerBox.SelectedItem = task.ConfirmationPeerId is long id
            ? confirmations.FirstOrDefault(x => x.Id == id && x.Kind == task.ConfirmationPeerKind)
            : confirmations[0];

        EnabledBox.IsChecked = task.Enabled;
        PeriodBox.SelectedIndex = task.Period == SchedulePeriod.Weekly ? 1 : 0;
        MondayBox.IsChecked = task.WeekDays.Contains(DayOfWeek.Monday);
        TuesdayBox.IsChecked = task.WeekDays.Contains(DayOfWeek.Tuesday);
        WednesdayBox.IsChecked = task.WeekDays.Contains(DayOfWeek.Wednesday);
        ThursdayBox.IsChecked = task.WeekDays.Contains(DayOfWeek.Thursday);
        FridayBox.IsChecked = task.WeekDays.Contains(DayOfWeek.Friday);
        SaturdayBox.IsChecked = task.WeekDays.Contains(DayOfWeek.Saturday);
        SundayBox.IsChecked = task.WeekDays.Contains(DayOfWeek.Sunday);
        UpdateWeekDaysAvailability();
        TimeBox.Text = task.Time.ToString(@"hh\:mm");
        MessageBox.Text = task.Message;
        ConfirmationEmailBox.Text = task.ConfirmationEmail;
        ConfirmationTextBox.Text = task.ConfirmationText;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (TargetBox.SelectedItem is not PeerOption target || target.Id is not long targetId)
                throw new InvalidOperationException(LocalizationManager.Text("SelectTargetChat"));
            if (!TimeSpan.TryParseExact(TimeBox.Text.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var time)
                || time >= TimeSpan.FromDays(1))
                throw new InvalidOperationException(LocalizationManager.Text("InvalidTimeFormat"));
            var message = MessageBox.Text.Trim();
            if (message.Length == 0) throw new InvalidOperationException(LocalizationManager.Text("MessageRequired"));
            var confirmation = ConfirmationPeerBox.SelectedItem as PeerOption;
            var period = PeriodBox.SelectedIndex == 1 ? SchedulePeriod.Weekly : SchedulePeriod.Daily;
            var weekDays = GetSelectedWeekDays();
            if (period == SchedulePeriod.Weekly && weekDays.Count == 0)
                throw new InvalidOperationException(LocalizationManager.Text("WeeklyDayRequired"));
            var executionDefinitionChanged =
                _task.ChatId != targetId
                || _task.ChatKind != target.Kind
                || _task.Time != time
                || _task.Period != period
                || !_task.WeekDays.OrderBy(x => x).SequenceEqual(weekDays.OrderBy(x => x))
                || !string.Equals(_task.Message, message, StringComparison.Ordinal);

            _task.Enabled = EnabledBox.IsChecked == true;
            _task.ChatId = targetId;
            _task.ChatKind = target.Kind;
            _task.ChatTitle = target.Name;
            _task.Time = time;
            _task.Period = period;
            _task.WeekDays = weekDays;
            _task.Message = message;
            _task.ConfirmationPeerId = confirmation?.Id;
            _task.ConfirmationPeerKind = confirmation?.Kind ?? "";
            _task.ConfirmationPeerTitle = confirmation?.Id is null ? "" : confirmation.Name;
            var confirmationEmail = ConfirmationEmailBox.Text.Trim();
            if (confirmationEmail.Length > 0 && !_emailConfigured)
                throw new InvalidOperationException(LocalizationManager.Text("EmailNotConfigured"));
            _task.ConfirmationEmail = confirmationEmail;
            _task.ConfirmationText = string.IsNullOrWhiteSpace(ConfirmationTextBox.Text)
                ? "签到完成：{群聊}，时间 {时间}"
                : ConfirmationTextBox.Text.Trim();
            if (executionDefinitionChanged) _task.LastSentDate = null;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, LocalizationManager.Text("EditTaskError"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void EnsureOption(List<PeerOption> options, long id, string kind, string name)
    {
        if (options.All(x => x.Id != id || x.Kind != kind)) options.Add(new(id, kind, name));
    }

    private void PeriodBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (WeekDaysPanel is not null) UpdateWeekDaysAvailability();
    }

    private void UpdateWeekDaysAvailability() => WeekDaysPanel.IsEnabled = PeriodBox.SelectedIndex == 1;

    private List<DayOfWeek> GetSelectedWeekDays()
    {
        var days = new List<DayOfWeek>();
        if (MondayBox.IsChecked == true) days.Add(DayOfWeek.Monday);
        if (TuesdayBox.IsChecked == true) days.Add(DayOfWeek.Tuesday);
        if (WednesdayBox.IsChecked == true) days.Add(DayOfWeek.Wednesday);
        if (ThursdayBox.IsChecked == true) days.Add(DayOfWeek.Thursday);
        if (FridayBox.IsChecked == true) days.Add(DayOfWeek.Friday);
        if (SaturdayBox.IsChecked == true) days.Add(DayOfWeek.Saturday);
        if (SundayBox.IsChecked == true) days.Add(DayOfWeek.Sunday);
        return days;
    }

    private sealed record PeerOption(long? Id, string Kind, string Name)
    {
        public override string ToString() => Name;
    }
}
