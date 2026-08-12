using System.Windows;

namespace PublishTool.Gui;

public enum GitConflictResolution
{
    Cancel,
    Discard,
    Stash,
    Commit,
    CheckoutAnyway,
}

/// <summary>
/// Shown either when a checkout is actually blocked by uncommitted local changes, or proactively
/// beforehand when there are uncommitted changes that would simply carry over onto the new branch
/// unnoticed. Either way it lets the user resolve it right here (discard/stash/commit just the
/// affected files) instead of switching to their IDE or a terminal. Purely a convenience: Cancel
/// always leaves the working tree untouched so they can just as easily handle it elsewhere.
/// </summary>
public partial class GitConflictDialog : Wpf.Ui.Controls.FluentWindow
{
    public GitConflictDialog(string branch, IReadOnlyList<string> files, bool isBlocking)
    {
        InitializeComponent();

        if (isBlocking)
        {
            Title = "Branch Checkout Blocked";
            MessageTextBlock.Text =
                $"Checking out '{branch}' would overwrite local changes to {files.Count} file(s). " +
                "Choose how to handle them, or Cancel to leave your working tree as-is and resolve it " +
                "yourself (e.g. in your IDE).";
            FilesHeaderTextBlock.Text = "Conflicting files";
            CheckoutAnywayButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            Title = "Uncommitted Changes";
            MessageTextBlock.Text =
                $"You have uncommitted changes to {files.Count} file(s). Checking out '{branch}' won't " +
                "necessarily overwrite them, but they'll silently carry over onto the new branch unless " +
                "you handle them first.";
            FilesHeaderTextBlock.Text = "Changed files";
            CheckoutAnywayButton.Visibility = Visibility.Visible;
        }

        FilesListBox.ItemsSource = files;
        CommitMessageTextBox.Text = $"WIP before switching to {branch}";
    }

    public GitConflictResolution Resolution { get; private set; } = GitConflictResolution.Cancel;

    public string CommitMessage => CommitMessageTextBox.Text;

    private void CheckoutAnywayButton_Click(object sender, RoutedEventArgs e)
    {
        Resolution = GitConflictResolution.CheckoutAnyway;
        DialogResult = true;
    }

    private void DiscardButton_Click(object sender, RoutedEventArgs e)
    {
        Resolution = GitConflictResolution.Discard;
        DialogResult = true;
    }

    private void StashButton_Click(object sender, RoutedEventArgs e)
    {
        Resolution = GitConflictResolution.Stash;
        DialogResult = true;
    }

    private void CommitButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(CommitMessageTextBox.Text))
        {
            MessageBox.Show("Enter a commit message.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Resolution = GitConflictResolution.Commit;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Resolution = GitConflictResolution.Cancel;
        DialogResult = false;
    }
}
