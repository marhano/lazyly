using System.Windows;

namespace PublishTool.Gui;

/// <summary>Lets the user pick all, one, or a subset of registered projects to export to a JSON
/// file -- so exporting never has to be all-or-nothing and a teammate can share just the project
/// they mean to, without dragging every other project's config along with it.</summary>
public partial class ExportProjectsDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly List<SelectableProjectViewModel> _rows;

    public ExportProjectsDialog(IEnumerable<string> projectNames)
    {
        InitializeComponent();

        _rows = projectNames
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new SelectableProjectViewModel { Name = n })
            .ToList();

        ProjectsListBox.ItemsSource = _rows;
    }

    /// <summary>The projects the user checked, populated once the dialog closes with a true
    /// DialogResult.</summary>
    public IReadOnlyList<string> SelectedProjectNames { get; private set; } = Array.Empty<string>();

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
        {
            row.IsChecked = true;
        }

        ProjectsListBox.Items.Refresh();
    }

    private void SelectNoneButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
        {
            row.IsChecked = false;
        }

        ProjectsListBox.Items.Refresh();
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _rows.Where(r => r.IsChecked).Select(r => r.Name).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Select at least one project to export.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedProjectNames = selected;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
