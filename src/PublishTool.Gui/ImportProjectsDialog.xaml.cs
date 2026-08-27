using System.Windows;
using PublishTool.Core;
using PublishTool.Core.Services;

namespace PublishTool.Gui;

/// <summary>Previews an export file's projects before committing anything, flagging which ones
/// already exist locally. Projects that would overwrite an existing config default to UNCHECKED --
/// the whole point of this dialog is to stop an import from silently clobbering a teammate's
/// config for a project they don't own.</summary>
public partial class ImportProjectsDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly List<SelectableProjectViewModel> _rows;

    public ImportProjectsDialog(ProjectConfigExportFile file, IReadOnlyList<ProjectImportPreviewItem> preview)
    {
        InitializeComponent();

        var overwriteCount = preview.Count(p => p.AlreadyExists);
        IntroTextBlock.Text =
            $"Exported {file.ExportedAtUtc.ToPhTime():g}. {preview.Count} project(s) in this file" +
            (overwriteCount > 0
                ? $", {overwriteCount} of which already exist locally -- those are unchecked by default so nothing gets overwritten unless you choose to."
                : ".");

        _rows = preview
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => new SelectableProjectViewModel
            {
                Name = p.Name,
                IsChecked = !p.AlreadyExists,
                StatusText = p.AlreadyExists ? "overwrites existing project" : null,
            })
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

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _rows.Where(r => r.IsChecked).Select(r => r.Name).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Select at least one project to import.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedProjectNames = selected;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
