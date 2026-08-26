using System.Windows;

namespace PublishTool.Gui;

/// <summary>
/// Enter (or clear) the keystore details used to sign an Android release build -- the same four
/// fields as Android Studio's own "Generate Signed Bundle / APK" dialog. Opened from the project's
/// Edit dialog (to save persistently) and, if a release publish needs signing but none is saved
/// yet, from the Publish tab itself (see MainWindow's ResolveAndroidSigningAsync).
/// </summary>
public partial class AndroidSigningDialog : Wpf.Ui.Controls.FluentWindow
{
    public AndroidSigningDialog(string? keystorePath, string? keyAlias, string? keystorePassword, string? keyPassword)
    {
        InitializeComponent();
        KeystorePathTextBox.Text = keystorePath ?? string.Empty;
        KeyAliasTextBox.Text = keyAlias ?? string.Empty;
        KeystorePasswordBox.Password = keystorePassword ?? string.Empty;
        KeyPasswordBox.Password = keyPassword ?? string.Empty;
    }

    public string? KeystorePath => string.IsNullOrWhiteSpace(KeystorePathTextBox.Text) ? null : KeystorePathTextBox.Text.Trim();

    public string? KeyAlias => string.IsNullOrWhiteSpace(KeyAliasTextBox.Text) ? null : KeyAliasTextBox.Text.Trim();

    public string? KeystorePassword => string.IsNullOrEmpty(KeystorePasswordBox.Password) ? null : KeystorePasswordBox.Password;

    public string? KeyPassword => string.IsNullOrEmpty(KeyPasswordBox.Password) ? null : KeyPasswordBox.Password;

    /// <summary>True if the user clicked Clear -- the caller should wipe all four saved fields
    /// rather than reading the (irrelevant) property values above.</summary>
    public bool WasCleared { get; private set; }

    private void BrowseKeystorePath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Keystore files (*.jks;*.keystore)|*.jks;*.keystore|All files (*.*)|*.*" };
        if (dialog.ShowDialog() == true)
        {
            KeystorePathTextBox.Text = dialog.FileName;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // Partial signing config isn't usable -- either all four fields are set, or none are
        // (equivalent to Clear).
        var fields = new[] { KeystorePath, KeyAlias, KeystorePassword, KeyPassword };
        if (fields.Any(f => f is not null) && fields.Any(f => f is null))
        {
            MessageBox.Show(
                "Fill in all four fields, or leave all of them blank to skip signing configuration.",
                "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        WasCleared = false;
        DialogResult = true;
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        WasCleared = true;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
