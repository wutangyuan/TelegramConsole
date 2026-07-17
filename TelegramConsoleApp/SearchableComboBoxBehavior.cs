using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
        private TextBox? _searchBox;
        private ListCollectionView? _view;
        private bool _applyingView;
        private bool _suppressTextChanged;

        public SearchState(ComboBox comboBox)
        {
            _comboBox = comboBox;
            _comboBox.IsEditable = false;
            _comboBox.IsTextSearchEnabled = false;
            _comboBox.DropDownOpened += ComboBox_DropDownOpened;
            _comboBox.DropDownClosed += ComboBox_DropDownClosed;
            _itemsSourceDescriptor = DependencyPropertyDescriptor.FromProperty(
                ItemsControl.ItemsSourceProperty, typeof(ComboBox));
            _itemsSourceDescriptor.AddValueChanged(comboBox, ItemsSourceChanged);
            ApplyItemsSource(comboBox.ItemsSource);
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
            _comboBox.Dispatcher.BeginInvoke(AttachAndFocusSearchBox, DispatcherPriority.ContextIdle);
        }

        private void AttachAndFocusSearchBox()
        {
            _comboBox.ApplyTemplate();
            var popup = _comboBox.Template.FindName("PART_Popup", _comboBox) as Popup;
            var editor = popup?.Child is null ? null : FindNamedTextBox(popup.Child, "PART_SearchBox");
            if (editor is null) return;
            if (!ReferenceEquals(_searchBox, editor))
            {
                if (_searchBox is not null)
                {
                    _searchBox.TextChanged -= SearchBox_TextChanged;
                    _searchBox.PreviewKeyDown -= SearchBox_PreviewKeyDown;
                }
                _searchBox = editor;
                _searchBox.TextChanged += SearchBox_TextChanged;
                _searchBox.PreviewKeyDown += SearchBox_PreviewKeyDown;
            }
            _suppressTextChanged = true;
            _searchBox.Clear();
            _suppressTextChanged = false;
            _searchBox.UpdateLayout();
            _searchBox.Focus();
            Keyboard.Focus(_searchBox);
        }

        private static TextBox? FindNamedTextBox(DependencyObject parent, string name)
        {
            if (parent is TextBox textBox && textBox.Name == name) return textBox;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                if (FindNamedTextBox(VisualTreeHelper.GetChild(parent, index), name) is { } match) return match;
            }
            return null;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressTextChanged || _view is null) return;
            var query = _searchBox?.Text.Trim() ?? "";
            _view.Filter = string.IsNullOrWhiteSpace(query)
                ? null
                : item => DisplayText(item).Contains(query, StringComparison.CurrentCultureIgnoreCase);
            _view.Refresh();
            if (_view.Count > 0) _view.MoveCurrentToFirst();
        }

        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                _comboBox.IsDropDownOpen = false;
                e.Handled = true;
                return;
            }
            if (e.Key != Key.Enter || _view?.CurrentItem is null) return;
            _comboBox.SelectedItem = _view.CurrentItem;
            _comboBox.IsDropDownOpen = false;
            e.Handled = true;
        }

        private void ComboBox_DropDownClosed(object? sender, EventArgs e)
        {
            ClearFilter();
            if (_searchBox is null) return;
            _suppressTextChanged = true;
            _searchBox.Clear();
            _suppressTextChanged = false;
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
            if (_view?.Filter is null) return;
            _view.Filter = null;
            _view.Refresh();
        }

        public void Dispose()
        {
            _itemsSourceDescriptor.RemoveValueChanged(_comboBox, ItemsSourceChanged);
            _comboBox.DropDownOpened -= ComboBox_DropDownOpened;
            _comboBox.DropDownClosed -= ComboBox_DropDownClosed;
            if (_searchBox is not null)
            {
                _searchBox.TextChanged -= SearchBox_TextChanged;
                _searchBox.PreviewKeyDown -= SearchBox_PreviewKeyDown;
            }
        }
    }
}
