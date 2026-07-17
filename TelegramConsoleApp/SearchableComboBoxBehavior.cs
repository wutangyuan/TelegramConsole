using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using ComboBox = System.Windows.Controls.ComboBox;
using KeyEventHandler = System.Windows.Input.KeyEventHandler;
using TextBox = System.Windows.Controls.TextBox;

namespace TelegramConsoleApp;

/// <summary>
/// Adds filtering to the standard editable area of a ComboBox. The editor remains in the
/// owner window while only the item list uses WPF's normal non-focusable popup, so IME and
/// window activation continue to behave like an ordinary input control.
/// </summary>
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
        private Window? _ownerWindow;
        private ListCollectionView? _view;
        private bool _applyingView;
        private bool _suppressTextChanged;
        private object? _firstMatch;

        public SearchState(ComboBox comboBox)
        {
            _comboBox = comboBox;
            _comboBox.IsEditable = true;
            _comboBox.IsReadOnly = false;
            _comboBox.IsTextSearchEnabled = false;
            _comboBox.IsSynchronizedWithCurrentItem = false;
            _comboBox.StaysOpenOnEdit = true;
            _comboBox.Loaded += ComboBox_Loaded;
            _comboBox.DropDownOpened += ComboBox_DropDownOpened;
            _comboBox.DropDownClosed += ComboBox_DropDownClosed;
            _itemsSourceDescriptor = DependencyPropertyDescriptor.FromProperty(
                ItemsControl.ItemsSourceProperty, typeof(ComboBox));
            _itemsSourceDescriptor.AddValueChanged(comboBox, ItemsSourceChanged);
            ApplyItemsSource(comboBox.ItemsSource);
            if (comboBox.IsLoaded) AttachEditor();
        }

        private void ComboBox_Loaded(object sender, RoutedEventArgs e) => AttachEditor();

        private void AttachEditor()
        {
            _comboBox.ApplyTemplate();
            var editor = _comboBox.Template.FindName("PART_EditableTextBox", _comboBox) as TextBox;
            if (editor is null || ReferenceEquals(editor, _editor)) return;
            DetachEditor();
            _editor = editor;
            _editor.IsReadOnly = false;
            _editor.TextChanged += Editor_TextChanged;
            _editor.AddHandler(Keyboard.PreviewKeyDownEvent,
                new KeyEventHandler(Editor_PreviewKeyDown), handledEventsToo: true);
            _editor.PreviewMouseLeftButtonDown += Editor_PreviewMouseLeftButtonDown;
            var owner = Window.GetWindow(_comboBox);
            if (!ReferenceEquals(owner, _ownerWindow))
            {
                DetachOwnerWindow();
                _ownerWindow = owner;
                _ownerWindow?.AddHandler(Keyboard.PreviewKeyDownEvent,
                    new KeyEventHandler(OwnerWindow_PreviewKeyDown), handledEventsToo: true);
            }
        }

        private void OwnerWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || _editor?.IsKeyboardFocusWithin != true || _firstMatch is null) return;
            CommitFirstMatch();
            e.Handled = true;
        }

        private void Editor_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_comboBox.IsDropDownOpen) _comboBox.IsDropDownOpen = true;
            _comboBox.Dispatcher.BeginInvoke(() =>
            {
                _editor?.Focus();
                _editor?.SelectAll();
            }, DispatcherPriority.Input);
        }

        private void ComboBox_DropDownOpened(object? sender, EventArgs e)
        {
            AttachEditor();
            ClearFilter();
            _comboBox.Dispatcher.BeginInvoke(() =>
            {
                _editor?.Focus();
                _editor?.SelectAll();
            }, DispatcherPriority.Input);
        }

        private void Editor_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressTextChanged || _view is null) return;
            if (!_comboBox.IsDropDownOpen && _editor?.IsKeyboardFocused != true) return;
            if (!_comboBox.IsDropDownOpen) _comboBox.IsDropDownOpen = true;
            var query = _editor?.Text.Trim() ?? "";
            if (_editor is not null && _comboBox.SelectedItem is not null &&
                !string.Equals(query, DisplayText(_comboBox.SelectedItem), StringComparison.CurrentCulture))
            {
                // ComboBox tries to restore the selected item's display text while the user
                // types. Detach the selection but preserve the text being composed.
                _suppressTextChanged = true;
                _comboBox.SelectedItem = null;
                _editor.Text = query;
                _editor.CaretIndex = _editor.Text.Length;
                _suppressTextChanged = false;
            }
            _view.Filter = string.IsNullOrWhiteSpace(query)
                ? null
                : item => DisplayText(item).Contains(query, StringComparison.CurrentCultureIgnoreCase);
            _view.Refresh();
            _firstMatch = _comboBox.Items.Count > 0 ? _comboBox.Items[0] : null;
        }

        private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                _comboBox.IsDropDownOpen = false;
                e.Handled = true;
                return;
            }
            if (e.Key != Key.Enter || _firstMatch is null) return;
            CommitFirstMatch();
            e.Handled = true;
        }

        private void CommitFirstMatch()
        {
            if (_firstMatch is null) return;
            var target = _firstMatch;
            _firstMatch = null;
            _comboBox.SelectedItem = target;
            _comboBox.IsDropDownOpen = false;
        }

        private void ComboBox_DropDownClosed(object? sender, EventArgs e)
        {
            ClearFilter();
            if (_editor is null) return;
            _suppressTextChanged = true;
            _editor.Text = DisplayText(_comboBox.SelectedItem);
            _editor.CaretIndex = _editor.Text.Length;
            _suppressTextChanged = false;
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

        private void DetachEditor()
        {
            if (_editor is null) return;
            _editor.TextChanged -= Editor_TextChanged;
            _editor.RemoveHandler(Keyboard.PreviewKeyDownEvent,
                new KeyEventHandler(Editor_PreviewKeyDown));
            _editor.PreviewMouseLeftButtonDown -= Editor_PreviewMouseLeftButtonDown;
            _editor = null;
        }

        private void DetachOwnerWindow()
        {
            if (_ownerWindow is null) return;
            _ownerWindow.RemoveHandler(Keyboard.PreviewKeyDownEvent,
                new KeyEventHandler(OwnerWindow_PreviewKeyDown));
            _ownerWindow = null;
        }

        public void Dispose()
        {
            _itemsSourceDescriptor.RemoveValueChanged(_comboBox, ItemsSourceChanged);
            _comboBox.Loaded -= ComboBox_Loaded;
            _comboBox.DropDownOpened -= ComboBox_DropDownOpened;
            _comboBox.DropDownClosed -= ComboBox_DropDownClosed;
            DetachEditor();
            DetachOwnerWindow();
        }
    }
}
