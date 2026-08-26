using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using PublishTool.Core.Models;

namespace PublishTool.Gui;

/// <summary>Shows the IIS tab's full Start/Stop/Removed/Recycled audit trail (newest-first), with a
/// live search filter -- opened from the IIS tab's "History" button. Global, not scoped to a
/// selected site/pool, since a removed site needs to stay visible in history.</summary>
public partial class IisAuditDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly ICollectionView _view;

    public IisAuditDialog(IReadOnlyList<IisAuditEntry> entries)
    {
        InitializeComponent();

        AuditDataGrid.ItemsSource = entries;
        _view = CollectionViewSource.GetDefaultView(entries);
        _view.Filter = FilterEntry;
    }

    private bool FilterEntry(object item)
    {
        var query = SearchTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(query) || item is not IisAuditEntry entry)
        {
            return true;
        }

        return entry.EntityName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               entry.Action.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               (entry.Details?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
               entry.PerformedBy.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void SearchTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => _view?.Refresh();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
