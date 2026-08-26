using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using PublishTool.Core.Models;

namespace PublishTool.Gui;

/// <summary>Shows the Projects tab's audit trail (newest-first), with a live search filter --
/// opened either scoped to one project (per-project "History" button) or unscoped, showing every
/// project's history (the standalone "All history" button), same optional-filter shape as
/// <see cref="DeploymentHistoryDialog"/> takes a specific site's history. Unlike
/// <see cref="FirewallAuditDialog"/>, this always receives the *complete* fetched log and filters
/// client-side by <paramref name="projectNameFilter"/> when given, rather than the caller
/// pre-filtering, so the same fetched list can back either mode without a second round-trip.</summary>
public partial class ProjectAuditDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly ICollectionView _view;
    private readonly string? _projectNameFilter;

    public ProjectAuditDialog(IReadOnlyList<ProjectAuditEntry> entries, string? projectNameFilter = null)
    {
        InitializeComponent();
        _projectNameFilter = projectNameFilter;

        Title = projectNameFilter is null ? "Project Audit Trail -- All Projects" : $"Project Audit Trail -- {projectNameFilter}";
        HeaderTextBlock.Text = projectNameFilter is null
            ? "Every project added, removed, published, deployed, or changed here -- who did it and when"
            : $"Every action recorded for '{projectNameFilter}' -- who did it and when";

        AuditDataGrid.ItemsSource = entries;
        _view = CollectionViewSource.GetDefaultView(entries);
        _view.Filter = FilterEntry;
    }

    private bool FilterEntry(object item)
    {
        if (item is not ProjectAuditEntry entry)
        {
            return true;
        }

        if (_projectNameFilter is not null && !string.Equals(entry.ProjectName, _projectNameFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var query = SearchTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            return true;
        }

        return entry.ProjectName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               entry.Action.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               (entry.Details?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
               entry.PerformedBy.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void SearchTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => _view?.Refresh();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
