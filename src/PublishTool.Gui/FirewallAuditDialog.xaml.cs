using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using PublishTool.Core.Models;

namespace PublishTool.Gui;

/// <summary>Shows the Firewall tab's full Add/Edit/Remove audit trail (newest-first), with a live
/// search filter -- opened from the Firewall tab's "History" button. Global, not scoped to a
/// selected rule, since a removed rule needs to stay visible in history.</summary>
public partial class FirewallAuditDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly ICollectionView _view;

    public FirewallAuditDialog(IReadOnlyList<FirewallAuditEntry> entries)
    {
        InitializeComponent();

        AuditDataGrid.ItemsSource = entries;
        _view = CollectionViewSource.GetDefaultView(entries);
        _view.Filter = FilterEntry;
    }

    private bool FilterEntry(object item)
    {
        var query = SearchTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(query) || item is not FirewallAuditEntry entry)
        {
            return true;
        }

        return entry.RuleName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               entry.Action.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               entry.PerformedBy.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void SearchTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => _view?.Refresh();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
