using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using PublishTool.Core.Models;

namespace PublishTool.Gui;

/// <summary>Shows one IIS site's full deployment history (newest-first), with a live search filter
/// -- opened from the IIS tab's "History" button. The caller resolves the history list itself
/// (local file read vs. the remote Hosting API, and any "server needs updating" messaging for a
/// 404 there) before constructing this -- the dialog just displays and filters what it's given.</summary>
public partial class DeploymentHistoryDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly ICollectionView _view;

    public DeploymentHistoryDialog(string siteName, IReadOnlyList<SiteDeploymentRecord> history)
    {
        InitializeComponent();
        TitleTextBlock.Text = $"Deployment history: {siteName}";

        HistoryDataGrid.ItemsSource = history;
        _view = CollectionViewSource.GetDefaultView(history);
        _view.Filter = FilterRecord;
    }

    private bool FilterRecord(object item)
    {
        var query = SearchTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(query) || item is not SiteDeploymentRecord record)
        {
            return true;
        }

        return record.Version.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               record.EnvironmentName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               record.DeployedBy.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void SearchTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => _view?.Refresh();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
