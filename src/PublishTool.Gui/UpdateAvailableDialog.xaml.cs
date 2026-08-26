using System.Windows;

namespace PublishTool.Gui;

/// <summary>Shown once a background-downloaded update is ready to install. "Restart Now" applies
/// it immediately; "Later" leaves it staged -- ExitFromTray applies it on the next real exit, so
/// picking "Later" never loses the update, just defers it.</summary>
public partial class UpdateAvailableDialog : Wpf.Ui.Controls.FluentWindow
{
    public UpdateAvailableDialog(string versionText, string? releaseNotes)
    {
        InitializeComponent();
        MessageTextBlock.Text = $"PublishTool {versionText} is ready to install.";
        ReleaseNotesTextBlock.Text = string.IsNullOrWhiteSpace(releaseNotes)
            ? "No release notes were provided for this update."
            : releaseNotes;
    }

    private void RestartNowButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void LaterButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
