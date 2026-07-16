using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace TelegramConsoleApp;

public partial class ProductivityWindow : Window
{
    private readonly ITelegramService _telegram;
    private readonly IReadOnlyList<DialogItem> _dialogs;
    private readonly AccountProfile _account;
    private readonly ISettingsStore _store;
    private readonly AppSettings _settings;
    private readonly IAppLogger _logger;
    private Guid? _editingRuleId;

    public ProductivityWindow(
        ITelegramService telegram,
        IReadOnlyList<DialogItem> dialogs,
        AccountProfile account,
        ISettingsStore store,
        AppSettings settings,
        IAppLogger logger)
    {
        InitializeComponent();
        _telegram = telegram;
        _dialogs = dialogs;
        _account = account;
        _store = store;
        _settings = settings;
        _logger = logger;

        var scopes = new[] { new SearchScope(LocalizationManager.Get("AllDialogs"), null) }
            .Concat(dialogs.Select(x => new SearchScope(x.Name, x))).ToArray();
        SearchScopeBox.ItemsSource = scopes;
        SearchScopeBox.SelectedIndex = 0;
        ServerScheduleDialogBox.ItemsSource = dialogs;
        DraftDialogBox.ItemsSource = dialogs;
        FolderDialogsList.ItemsSource = dialogs;
        ForwardTargetBox.ItemsSource = dialogs;
        RuleTargetBox.ItemsSource = dialogs;
        RuleChatBox.ItemsSource = scopes;
        RuleChatBox.SelectedIndex = 0;
        RuleTriggerBox.ItemsSource = Enum.GetValues<AutomationTrigger>()
            .Select(x => new EnumOption<AutomationTrigger>(x, LocalizationManager.Get("AutomationTrigger" + x))).ToArray();
        RuleTriggerBox.SelectedIndex = 0;
        RuleActionBox.ItemsSource = Enum.GetValues<AutomationAction>()
            .Select(x => new EnumOption<AutomationAction>(x, LocalizationManager.Get("AutomationAction" + x))).ToArray();
        RuleActionBox.SelectedIndex = 2;
        ServerScheduleDatePicker.SelectedDate = DateTime.Today.AddDays(1);
        if (dialogs.Count > 0)
        {
            ServerScheduleDialogBox.SelectedIndex = 0;
            DraftDialogBox.SelectedIndex = 0;
            ForwardTargetBox.SelectedIndex = 0;
            RuleTargetBox.SelectedIndex = 0;
        }
        RenderRules();
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var query = SearchTextBox.Text.Trim();
        if (query.Length == 0) throw new InvalidOperationException(LocalizationManager.Get("SearchHint"));
        var dialog = (SearchScopeBox.SelectedItem as SearchScope)?.Dialog;
        var topicId = (TopicBox.SelectedItem as ForumTopicItem)?.Id;
        var results = await _telegram.SearchMessagesAsync(query, dialog);
        if (topicId is int id) results = results.Where(x => x.TopicId == id).ToArray();
        SearchResultList.ItemsSource = results.Select(x => new SearchRow(
            x.ChatId, x.ChatKind, x.ChatTitle, x.MessageId, x.Time.ToString("yyyy-MM-dd HH:mm:ss"),
            x.Sender, x.Text, x.IsOutgoing, x.TopicId, x.TopicId?.ToString() ?? "-", x.Source)).ToArray();
    });

    private async void LoadTopics_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var dialog = (SearchScopeBox.SelectedItem as SearchScope)?.Dialog
                     ?? throw new InvalidOperationException(LocalizationManager.Get("SelectSpecificChat"));
        if (!dialog.IsForum) throw new InvalidOperationException(LocalizationManager.Get("ChatIsNotForum"));
        TopicBox.ItemsSource = await _telegram.LoadForumTopicsAsync(dialog);
        if (TopicBox.Items.Count > 0) TopicBox.SelectedIndex = 0;
    });

    private void SearchScopeBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (LoadTopicsButton is null || TopicBox is null) return;
        var isForum = (SearchScopeBox.SelectedItem as SearchScope)?.Dialog?.IsForum == true;
        LoadTopicsButton.IsEnabled = isForum;
        TopicBox.IsEnabled = isForum;
        if (!isForum) TopicBox.ItemsSource = null;
    }

    private async void Reply_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var row = SelectedMessage();
        await _telegram.SendReplyAsync(DialogOf(row), row.MessageId, RequiredOperationText());
    });

    private async void QuoteReply_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var row = SelectedMessage();
        await _telegram.SendReplyAsync(DialogOf(row), row.MessageId, RequiredOperationText(), row.Text);
    });

    private async void EditMessage_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var row = SelectedMessage();
        if (!row.IsOutgoing) throw new InvalidOperationException(LocalizationManager.Get("EditOwnOnly"));
        await _telegram.EditMessageAsync(DialogOf(row), row.MessageId, RequiredOperationText());
    });

    private async void DeleteMessage_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var rows = SearchResultList.SelectedItems.Cast<SearchRow>().ToArray();
        if (rows.Length == 0) throw new InvalidOperationException(LocalizationManager.Get("SelectMessage"));
        if (System.Windows.MessageBox.Show(LocalizationManager.Format("ConfirmDeleteMessages", rows.Length),
                LocalizationManager.Get("ConfirmTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        foreach (var group in rows.GroupBy(x => (x.ChatId, x.ChatKind, x.ChatTitle)))
            await _telegram.DeleteMessagesAsync(
                new DialogItem(group.Key.ChatTitle, group.Key.ChatId, group.Key.ChatKind, group.Key.ChatKind != "User"),
                group.Select(x => x.MessageId).ToArray());
        SearchResultList.ItemsSource = null;
    });

    private async void ForwardMessage_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var rows = SearchResultList.SelectedItems.Cast<SearchRow>().ToArray();
        if (rows.Length == 0) throw new InvalidOperationException(LocalizationManager.Get("SelectMessage"));
        var target = ForwardTargetBox.SelectedItem as DialogItem
                     ?? throw new InvalidOperationException(LocalizationManager.Get("SelectForwardTarget"));
        foreach (var group in rows.GroupBy(x => (x.ChatId, x.ChatKind, x.ChatTitle)))
            await _telegram.ForwardMessagesAsync(
                new DialogItem(group.Key.ChatTitle, group.Key.ChatId, group.Key.ChatKind, group.Key.ChatKind != "User"),
                group.Select(x => x.MessageId).ToArray(), target);
    });

    private void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var row = SelectedMessage();
            var link = _telegram.GetMessageLink(DialogOf(row), row.MessageId);
            if (link.Length == 0) throw new InvalidOperationException(LocalizationManager.Get("NoPublicMessageLink"));
            System.Windows.Clipboard.SetText(link);
        }
        catch (Exception ex) { ShowError(UserMessageFormatter.From(ex)); }
    }

    private async void CreateServerSchedule_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var dialog = ServerScheduleDialogBox.SelectedItem as DialogItem
                     ?? throw new InvalidOperationException(LocalizationManager.Get("SelectTargetChat"));
        if (ServerScheduleDatePicker.SelectedDate is not DateTime date ||
            !TimeSpan.TryParseExact(ServerScheduleTimeBox.Text.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var time))
            throw new InvalidOperationException(LocalizationManager.Get("InvalidDateTime"));
        var text = ServerScheduleTextBox.Text.Trim();
        if (text.Length == 0) throw new InvalidOperationException(LocalizationManager.Get("MessageRequired"));
        await _telegram.ScheduleServerMessageAsync(dialog, text, date.Date + time);
        await RefreshServerSchedulesAsync();
    });

    private async void RefreshServerSchedules_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(RefreshServerSchedulesAsync);

    private async Task RefreshServerSchedulesAsync()
    {
        var dialog = ServerScheduleDialogBox.SelectedItem as DialogItem
                     ?? throw new InvalidOperationException(LocalizationManager.Get("SelectTargetChat"));
        var items = await _telegram.LoadServerScheduledMessagesAsync(dialog);
        ServerScheduleList.ItemsSource = items.Select(x => new ServerScheduleRow(
            x.ChatId, x.ChatKind, x.ChatTitle, x.MessageId, x.SendAt.ToString("yyyy-MM-dd HH:mm:ss"), x.Text)).ToArray();
    }

    private async void DeleteServerSchedules_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var selected = ServerScheduleList.SelectedItems.Cast<ServerScheduleRow>().ToArray();
        if (selected.Length == 0) throw new InvalidOperationException(LocalizationManager.Get("SelectServerSchedule"));
        var dialog = ServerScheduleDialogBox.SelectedItem as DialogItem
                     ?? throw new InvalidOperationException(LocalizationManager.Get("SelectTargetChat"));
        await _telegram.DeleteServerScheduledMessagesAsync(dialog, selected.Select(x => x.MessageId).ToArray());
        await RefreshServerSchedulesAsync();
    });

    private void SaveRule_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = RuleNameBox.Text.Trim();
            if (name.Length == 0) throw new InvalidOperationException(LocalizationManager.Get("RuleNameRequired"));
            var trigger = ((EnumOption<AutomationTrigger>)RuleTriggerBox.SelectedItem).Value;
            var pattern = RulePatternBox.Text.Trim();
            var scope = RuleChatBox.SelectedItem as SearchScope;
            if (trigger == AutomationTrigger.Chat && scope?.Dialog is null)
                throw new InvalidOperationException(LocalizationManager.Get("SpecificChatRuleRequired"));
            if (trigger is not AutomationTrigger.Mention and not AutomationTrigger.Chat && pattern.Length == 0)
                throw new InvalidOperationException(LocalizationManager.Get("RulePatternRequired"));
            if (trigger == AutomationTrigger.RegularExpression)
                _ = System.Text.RegularExpressions.Regex.Match("", pattern, System.Text.RegularExpressions.RegexOptions.None,
                    TimeSpan.FromMilliseconds(500));
            var action = ((EnumOption<AutomationAction>)RuleActionBox.SelectedItem).Value;
            var target = RuleTargetBox.SelectedItem as DialogItem;
            if (action == AutomationAction.Telegram && target is null)
                throw new InvalidOperationException(LocalizationManager.Get("SelectNotificationTarget"));
            if (action == AutomationAction.Email && string.IsNullOrWhiteSpace(RuleEmailBox.Text))
                throw new InvalidOperationException(LocalizationManager.Get("EmailRequired"));
            var rule = _editingRuleId is Guid id
                ? _account.AutomationRules.First(x => x.Id == id)
                : new AutomationRule();
            rule.Name = name;
            rule.Trigger = trigger;
            rule.Pattern = pattern;
            rule.ChatId = scope?.Dialog?.Id;
            rule.Action = action;
            rule.TargetPeerId = target?.Id;
            rule.TargetPeerKind = target?.Kind ?? "";
            rule.TargetPeerTitle = target?.Name ?? "";
            rule.EmailRecipient = RuleEmailBox.Text.Trim();
            rule.MessageTemplate = RuleTemplateBox.Text.Trim();
            if (_editingRuleId is null) _account.AutomationRules.Add(rule);
            _editingRuleId = null;
            SaveRules();
            ClearRuleEditor();
        }
        catch (Exception ex) { ShowError(UserMessageFormatter.From(ex)); }
    }

    private void LoadRule_Click(object sender, RoutedEventArgs e)
    {
        if (RuleList.SelectedItem is not RuleRow row) { ShowError(LocalizationManager.Get("SelectRule")); return; }
        var rule = _account.AutomationRules.First(x => x.Id == row.Id);
        _editingRuleId = rule.Id;
        RuleNameBox.Text = rule.Name;
        RuleTriggerBox.SelectedItem = RuleTriggerBox.Items.Cast<EnumOption<AutomationTrigger>>()
            .First(x => x.Value == rule.Trigger);
        RulePatternBox.Text = rule.Pattern;
        RuleChatBox.SelectedItem = RuleChatBox.Items.Cast<SearchScope>().FirstOrDefault(x => x.Dialog?.Id == rule.ChatId) ?? RuleChatBox.Items[0];
        RuleActionBox.SelectedItem = RuleActionBox.Items.Cast<EnumOption<AutomationAction>>()
            .First(x => x.Value == rule.Action);
        RuleTargetBox.SelectedItem = _dialogs.FirstOrDefault(x => x.Id == rule.TargetPeerId && x.Kind == rule.TargetPeerKind);
        RuleEmailBox.Text = rule.EmailRecipient;
        RuleTemplateBox.Text = rule.MessageTemplate;
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        var ids = RuleList.SelectedItems.Cast<RuleRow>().Select(x => x.Id).ToHashSet();
        if (ids.Count == 0) { ShowError(LocalizationManager.Get("SelectRule")); return; }
        _account.AutomationRules.RemoveAll(x => ids.Contains(x.Id));
        SaveRules();
    }

    private void ToggleRule_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RuleList.SelectedItem is not RuleRow row) return;
        var rule = _account.AutomationRules.First(x => x.Id == row.Id);
        rule.Enabled = !rule.Enabled;
        SaveRules();
    }

    private void SaveRules()
    {
        _store.Save(_settings);
        _telegram.ConfigureAutomationRules(_account.AutomationRules);
        RenderRules();
    }

    private void RenderRules() => RuleList.ItemsSource = _account.AutomationRules.Select(x => new RuleRow(
        x.Id, x.Enabled ? LocalizationManager.Get("Enabled") : LocalizationManager.Get("Disabled"),
        x.Name, LocalizationManager.Get("AutomationTrigger" + x.Trigger), x.Pattern,
        LocalizationManager.Get("AutomationAction" + x.Action), x.MessageTemplate)).ToArray();

    private void ClearRuleEditor()
    {
        RuleNameBox.Clear(); RulePatternBox.Clear(); RuleEmailBox.Clear();
    }

    private async void SaveDraft_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var dialog = DraftDialogBox.SelectedItem as DialogItem
                     ?? throw new InvalidOperationException(LocalizationManager.Get("SelectTargetChat"));
        await _telegram.SaveCloudDraftAsync(dialog, DraftTextBox.Text);
        SetStatus(LocalizationManager.Format("CloudDraftSaved", dialog.Name));
    });

    private async void LoadDraft_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var dialog = DraftDialogBox.SelectedItem as DialogItem
                     ?? throw new InvalidOperationException(LocalizationManager.Get("SelectTargetChat"));
        var draft = await _telegram.LoadCloudDraftAsync(dialog);
        DraftTextBox.Text = draft;
        SetStatus(string.IsNullOrEmpty(draft)
            ? LocalizationManager.Format("CloudDraftEmpty", dialog.Name)
            : LocalizationManager.Format("CloudDraftLoaded", dialog.Name, draft.Length));
        DraftTextBox.Focus();
        DraftTextBox.CaretIndex = DraftTextBox.Text.Length;
    });

    private async void ClearDraft_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var dialog = DraftDialogBox.SelectedItem as DialogItem
                     ?? throw new InvalidOperationException(LocalizationManager.Get("SelectTargetChat"));
        await _telegram.SaveCloudDraftAsync(dialog, "");
        DraftTextBox.Clear();
        SetStatus(LocalizationManager.Format("CloudDraftCleared", dialog.Name));
    });

    private async void CreateFolder_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var dialogs = FolderDialogsList.SelectedItems.Cast<DialogItem>().ToArray();
        await _telegram.CreateDialogFolderAsync(FolderNameBox.Text.Trim(), dialogs);
        await RefreshFoldersAsync();
    });

    private async void RefreshFolders_Click(object sender, RoutedEventArgs e) => await RunAsync(RefreshFoldersAsync);

    private async Task RefreshFoldersAsync() => FolderList.ItemsSource = await _telegram.LoadDialogFoldersAsync();

    private void SetStatus(string message) => ProductivityStatusText.Text = message;

    private void OpenGuide_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "docs", "EFFICIENCY_TOOLS.md");
            if (!File.Exists(path)) throw new FileNotFoundException(LocalizationManager.Get("GuideNotFound"), path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError(UserMessageFormatter.From(ex)); }
    }

    private SearchRow SelectedMessage() => SearchResultList.SelectedItem as SearchRow
        ?? throw new InvalidOperationException(LocalizationManager.Get("SelectMessage"));

    private static DialogItem DialogOf(SearchRow row) =>
        new(row.ChatTitle, row.ChatId, row.ChatKind, row.ChatKind != "User");

    private string RequiredOperationText()
    {
        var text = OperationTextBox.Text.Trim();
        return text.Length == 0 ? throw new InvalidOperationException(LocalizationManager.Get("MessageRequired")) : text;
    }

    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            if (ex is not InvalidOperationException)
                _logger.Error("Productivity", "效率工具操作失败", ex);
            ShowError(UserMessageFormatter.From(ex));
        }
    }

    private static void ShowError(string message) => System.Windows.MessageBox.Show(
        message, LocalizationManager.Get("OperationFailed"), MessageBoxButton.OK, MessageBoxImage.Error);

    private sealed record SearchScope(string Name, DialogItem? Dialog);
    private sealed record SearchRow(long ChatId, string ChatKind, string ChatTitle, int MessageId,
        string TimeText, string Sender, string Text, bool IsOutgoing, int? TopicId, string TopicText, string Source);
    private sealed record ServerScheduleRow(long ChatId, string ChatKind, string ChatTitle, int MessageId, string SendAtText, string Text);
    private sealed record RuleRow(Guid Id, string EnabledText, string Name, string TriggerText, string Pattern, string ActionText, string Template);
    private sealed record EnumOption<T>(T Value, string Name) where T : struct, Enum;
}
