using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace TelegramConsoleApp;

public static class SearchableComboBoxBehavior
{
    private static readonly ConditionalWeakTable<ComboBox, SearchState> States = new();

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SearchableComboBoxBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty SearchPathProperty = DependencyProperty.RegisterAttached(
        "SearchPath", typeof(string), typeof(SearchableComboBoxBehavior), new PropertyMetadata(""));

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetSearchPath(DependencyObject element, string value) => element.SetValue(SearchPathProperty, value);
    public static string GetSearchPath(DependencyObject element) => (string)element.GetValue(SearchPathProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox comboBox) return;
        if ((bool)e.NewValue)
        {
            if (!States.TryGetValue(comboBox, out _)) States.Add(comboBox, new SearchState(comboBox));
        }
        else if (States.TryGetValue(comboBox, out var state))
        {
            state.Dispose();
            States.Remove(comboBox);
        }
    }

    private sealed class SearchState : IDisposable
    {
        private readonly ComboBox _comboBox;
        private readonly DependencyPropertyDescriptor _itemsSourceDescriptor;
        private TextBox? _editor;
        private ListCollectionView? _view;
        private bool _applyingView;

        public SearchState(ComboBox comboBox)
        {
            _comboBox = comboBox;
            _comboBox.IsEditable = true;
            _comboBox.IsTextSearchEnabled = false;
            _comboBox.StaysOpenOnEdit = true;
            _comboBox.Loaded += ComboBox_Loaded;
            _comboBox.DropDownOpened += ComboBox_DropDownOpened;
            _comboBox.DropDownClosed += ComboBox_DropDownClosed;
            _comboBox.SelectionChanged += ComboBox_SelectionChanged;
            _comboBox.PreviewKeyDown += ComboBox_PreviewKeyDown;
            _itemsSourceDescriptor = DependencyPropertyDescriptor.FromProperty(
                ItemsControl.ItemsSourceProperty, typeof(ComboBox));
            _itemsSourceDescriptor.AddValueChanged(_comboBox, ItemsSourceChanged);
            ApplyItemsSource(_comboBox.ItemsSource);
        }

        private void ComboBox_Loaded(object sender, RoutedEventArgs e)
        {
            _comboBox.ApplyTemplate();
            _editor = _comboBox.Template.FindName("PART_EditableTextBox", _comboBox) as TextBox;
            if (_editor is not null) _editor.TextChanged += Editor_TextChanged;
            SyncSelectedText();
        }

        private void ItemsSourceChanged(object? sender, EventArgs e)
        {
            if (_applyingView || ReferenceEquals(_comboBox.ItemsSource, _view)) return;
            ApplyItemsSource(_comboBox.ItemsSource);
        }

        private void ApplyItemsSource(IEnumerable? source)
        {
            if (source is null) return;
            var selected = _comboBox.SelectedItem;
            var items = source.Cast<object>().ToList();
            _view = new ListCollectionView(items);
            _applyingView = true;
            try
            {
                _comboBox.ItemsSource = _view;
                if (selected is not null && items.Contains(selected)) _comboBox.SelectedItem = selected;
            }
            finally
            {
                _applyingView = false;
            }
        }

        private void ComboBox_DropDownOpened(object? sender, EventArgs e)
        {
            ClearFilter();
            _editor?.SelectAll();
        }

        private void ComboBox_DropDownClosed(object? sender, EventArgs e) => ClearFilter();

        private void ComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncSelectedText();

        private void SyncSelectedText()
        {
            if (_comboBox.SelectedItem is not null) _comboBox.Text = DisplayText(_comboBox.SelectedItem);
        }

        private void Editor_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_comboBox.IsDropDownOpen || _view is null) return;
            var query = _editor?.Text.Trim() ?? "";
            _view.Filter = string.IsNullOrWhiteSpace(query)
                ? null
                : item => DisplayText(item).Contains(query, StringComparison.CurrentCultureIgnoreCase);
            _view.Refresh();
        }

        private void ComboBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || !_comboBox.IsDropDownOpen) return;
            _comboBox.IsDropDownOpen = false;
            ClearFilter();
            e.Handled = true;
        }

        private string DisplayText(object? item)
        {
            if (item is null) return "";
            var path = GetSearchPath(_comboBox);
            if (string.IsNullOrWhiteSpace(path)) path = _comboBox.DisplayMemberPath;
            if (string.IsNullOrWhiteSpace(path)) return item.ToString() ?? "";
            object? value = item;
            foreach (var segment in path.Split('.'))
            {
                if (value is null) break;
                value = value.GetType().GetProperty(segment, BindingFlags.Instance | BindingFlags.Public)?.GetValue(value);
            }
            return value?.ToString() ?? "";
        }

        private void ClearFilter()
        {
            if (_view is null || _view.Filter is null) return;
            _view.Filter = null;
            _view.Refresh();
        }

        public void Dispose()
        {
            _itemsSourceDescriptor.RemoveValueChanged(_comboBox, ItemsSourceChanged);
            _comboBox.Loaded -= ComboBox_Loaded;
            _comboBox.DropDownOpened -= ComboBox_DropDownOpened;
            _comboBox.DropDownClosed -= ComboBox_DropDownClosed;
            _comboBox.SelectionChanged -= ComboBox_SelectionChanged;
            _comboBox.PreviewKeyDown -= ComboBox_PreviewKeyDown;
            if (_editor is not null) _editor.TextChanged -= Editor_TextChanged;
        }
    }
}
