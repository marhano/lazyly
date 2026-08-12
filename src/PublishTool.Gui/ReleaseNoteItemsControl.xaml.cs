using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PublishTool.Gui;

/// <summary>
/// A small "add a line, see the list, remove a line" editor used four times on the Publish tab
/// -- once per release notes section (Features, Fixes, Other Updates, Backlog Items).
/// </summary>
public partial class ReleaseNoteItemsControl : UserControl
{
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(ReleaseNoteItemsControl),
        new PropertyMetadata(string.Empty, OnHeaderChanged));

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

    public void Clear() => Items.Clear();

    private static void OnHeaderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ReleaseNoteItemsControl)d).HeaderTextBlock.Text = (string)e.NewValue;

    private void AddButton_Click(object sender, RoutedEventArgs e) => AddFromInput();

    private void InputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddFromInput();
        }
    }

    private void AddFromInput()
    {
        var text = InputTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Items.Add(text);
        InputTextBox.Clear();
        InputTextBox.Focus();
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsListBox.SelectedItem is string item)
        {
            Items.Remove(item);
        }
    }
}
