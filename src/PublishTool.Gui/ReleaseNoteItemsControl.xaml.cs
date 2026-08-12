using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PublishTool.Gui;

/// <summary>
/// A small "add a line, see the list, edit or remove a line" editor used four times on the
/// Publish tab -- once per release notes section (Features, Fixes, Other Updates, Backlog Items).
/// Clicking a list item loads it back into the input box for editing; the Add button becomes
/// "Update" while an item is selected, and commits the edit in place instead of appending.
/// </summary>
public partial class ReleaseNoteItemsControl : UserControl
{
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(ReleaseNoteItemsControl),
        new PropertyMetadata(string.Empty, OnHeaderChanged));

    private int? _editingIndex;

    public ReleaseNoteItemsControl()
    {
        InitializeComponent();
        ItemsListBox.ItemsSource = Items;
    }

    public ObservableCollection<string> Items { get; } = new();

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public void Clear()
    {
        Items.Clear();
        CancelEdit();
    }

    private static void OnHeaderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ReleaseNoteItemsControl)d).HeaderTextBlock.Text = (string)e.NewValue;

    private void AddButton_Click(object sender, RoutedEventArgs e) => CommitInput();

    private void InputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitInput();
        }
        else if (e.Key == Key.Escape)
        {
            CancelEdit();
        }
    }

    private void CommitInput()
    {
        var text = InputTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (_editingIndex is { } index && index >= 0 && index < Items.Count)
        {
            Items[index] = text;
        }
        else
        {
            Items.Add(text);
        }

        CancelEdit();
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsListBox.SelectedItem is string item)
        {
            Items.Remove(item);
        }

        CancelEdit();
    }

    private void ItemsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ItemsListBox.SelectedIndex is var index && index >= 0)
        {
            _editingIndex = index;
            InputTextBox.Text = Items[index];
            InputTextBox.Focus();
            InputTextBox.CaretIndex = InputTextBox.Text.Length;
            AddButton.Content = "Update";
        }
    }

    private void CancelEdit()
    {
        _editingIndex = null;
        InputTextBox.Clear();
        ItemsListBox.SelectedIndex = -1;
        AddButton.Content = "Add";
    }
}
