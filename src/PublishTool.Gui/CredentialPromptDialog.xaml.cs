using System.Windows;

namespace PublishTool.Gui;

/// <summary>Prompts for the password to connect to a remote Event Log machine, when the project
/// has a username configured but no saved (or successfully decrypted) password. "Remember" saves
/// it DPAPI-encrypted for next time; leaving it unchecked prompts again on every connection.</summary>
public partial class CredentialPromptDialog : Wpf.Ui.Controls.FluentWindow
{
    public CredentialPromptDialog(string machineName, string username)
    {
        InitializeComponent();
        MessageTextBlock.Text = $"Enter the password for '{username}' to read the event log on '{machineName}'.";
    }

    public string Password => PasswordBox.Password;

    public bool RememberPassword => RememberCheckBox.IsChecked == true;

    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(PasswordBox.Password))
        {
            MessageBox.Show("Enter a password.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
