using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Windows;
using PublishTool.Core;
using PublishTool.Core.Models;
using PublishTool.Core.Services;

namespace PublishTool.Gui;

/// <summary>
/// Deploys an arbitrary folder or zip file straight into an IIS site, creating the site (and its
/// own app pool) if it doesn't exist yet -- for a quick ad hoc deploy, or standing up a brand-new
/// site, that doesn't need a registered project at all. Works in both local and remote mode:
/// locally this reuses <see cref="BuildDeployer"/> directly, exactly like every other deploy path in
/// the app; remotely the source gets zipped (if it isn't already) and uploaded to a new
/// <c>/api/iis/manual-deploy</c> endpoint that does the same thing on the dev server's own machine.
/// </summary>
public partial class ManualDeployDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly IOutputSink _output;
    private readonly bool _remoteMode;
    private readonly ObservableCollection<IisBinding> _bindings = new();

    public ManualDeployDialog(IEnumerable<string> siteNames, string? preselectedSite, IOutputSink output)
    {
        InitializeComponent();
        _output = output;
        _remoteMode = IsRemoteModeActive(out _);

        SiteComboBox.ItemsSource = siteNames.ToList();
        if (preselectedSite is not null)
        {
            SiteComboBox.Text = preselectedSite;
        }

        BindingsDataGrid.ItemsSource = _bindings;
        NewSiteOptionsPanel.Visibility = Visibility.Collapsed;

        if (_remoteMode)
        {
            DescriptionTextBlock.Text += " Deploying to the dev server -- the physical path is a folder on ITS machine, not yours.";
            BrowsePhysicalPathButton.Visibility = Visibility.Collapsed;
        }
    }

    private static bool IsRemoteModeActive(out AppSettings settings)
    {
        settings = AppSettings.Load(AppSettings.DefaultPath);
        return settings.UseRemoteMode && !string.IsNullOrWhiteSpace(settings.RemoteHostingUrl);
    }

#pragma warning disable CA1416 // DPAPI is Windows-only; this whole GUI only ever runs on Windows.
    private static string? DecryptRemoteHostingApiKey(AppSettings settings) =>
        settings.RemoteHostingProtectedApiKey is null
            ? null
            : SecretProtector.TryUnprotect(settings.RemoteHostingProtectedApiKey, SecretProtector.RemoteHostingPurpose);
#pragma warning restore CA1416

    private void AutoCreateSiteToggle_Toggled(object sender, RoutedEventArgs e) =>
        NewSiteOptionsPanel.Visibility = AutoCreateSiteToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void AddBindingButton_Click(object sender, RoutedEventArgs e) =>
        _bindings.Add(new IisBinding { Protocol = "http", IpAddress = "*", Port = 80, HostName = null });

    private void RemoveBindingButton_Click(object sender, RoutedEventArgs e)
    {
        if (BindingsDataGrid.SelectedItem is IisBinding binding)
        {
            _bindings.Remove(binding);
        }
    }

    private void BrowsePhysicalPathButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog();
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            PhysicalPathTextBox.Text = dialog.SelectedPath;
        }
    }

    private void BrowseSourcePathButton_Click(object sender, RoutedEventArgs e)
    {
        if (SourceZipRadio.IsChecked == true)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Zip files (*.zip)|*.zip|All files (*.*)|*.*" };
            if (dialog.ShowDialog() == true)
            {
                SourcePathTextBox.Text = dialog.FileName;
            }
        }
        else
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog();
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                SourcePathTextBox.Text = dialog.SelectedPath;
            }
        }
    }

    private async void DeployButton_Click(object sender, RoutedEventArgs e)
    {
        BindingsDataGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
        BindingsDataGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);

        var siteName = SiteComboBox.Text?.Trim();
        var physicalPath = PhysicalPathTextBox.Text?.Trim();
        var sourcePath = SourcePathTextBox.Text?.Trim();
        var autoCreateSite = AutoCreateSiteToggle.IsChecked == true;

        if (string.IsNullOrWhiteSpace(siteName) || string.IsNullOrWhiteSpace(physicalPath) || string.IsNullOrWhiteSpace(sourcePath))
        {
            MessageBox.Show("Fill in the site name, physical path, and source path first.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (autoCreateSite && _bindings.Count == 0)
        {
            MessageBox.Show(
                "Auto-create site is on but no bindings were added. Add at least one, or turn it off.",
                "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var isZip = SourceZipRadio.IsChecked == true;
        if (isZip && !File.Exists(sourcePath))
        {
            MessageBox.Show($"'{sourcePath}' isn't a file that exists.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!isZip && !Directory.Exists(sourcePath))
        {
            MessageBox.Show($"'{sourcePath}' isn't a folder that exists.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            $"Deploy '{sourcePath}' to '{siteName}' ({physicalPath}){(_remoteMode ? " on the dev server" : "")}? This overwrites whatever is currently there.",
            "PublishTool", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        var label = string.IsNullOrWhiteSpace(LabelTextBox.Text) ? $"manual-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}" : LabelTextBox.Text.Trim();
        var poolTemplate = (PoolTemplateComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string == "NoManagedCode"
            ? AppPoolRuntimeTemplate.NoManagedCode
            : AppPoolRuntimeTemplate.DotNetFramework;

        IsEnabled = false;
        string? tempDir = null;
        try
        {
            if (_remoteMode)
            {
                var settings = AppSettings.Load(AppSettings.DefaultPath);
                var apiKey = DecryptRemoteHostingApiKey(settings);

                var zipToUpload = sourcePath;
                if (!isZip)
                {
                    tempDir = Path.Combine(Path.GetTempPath(), "PublishTool", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);
                    zipToUpload = Path.Combine(tempDir, "manual-deploy.zip");
                    _output.Stage($"Zipping '{sourcePath}'...");
                    await Task.Run(() => ZipFile.CreateFromDirectory(sourcePath, zipToUpload));
                }

                _output.Stage($"Uploading and deploying to '{siteName}' on the dev server...");
                await new RemoteHostingClient().ManualDeployRemoteAsync(
                    settings.RemoteHostingUrl!, apiKey, zipToUpload, siteName, physicalPath, autoCreateSite,
                    _bindings.ToList(), poolTemplate, label, Environment.UserName);
            }
            else
            {
                var sourceDir = sourcePath;
                if (isZip)
                {
                    tempDir = Path.Combine(Path.GetTempPath(), "PublishTool", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);
                    _output.Stage($"Extracting '{sourcePath}'...");
                    await Task.Run(() => ZipFile.ExtractToDirectory(sourcePath, tempDir));
                    sourceDir = tempDir;
                }

                await new BuildDeployer(_output).DeployAsync(
                    siteName, physicalPath, _bindings.ToList(), autoCreateSite, sourceDir,
                    new SiteDeploymentRecord
                    {
                        SiteName = siteName,
                        ProjectName = "(manual)",
                        Version = label,
                        EnvironmentName = "(manual)",
                        DeployedAtUtc = DateTimeOffset.UtcNow,
                        DeployedBy = Environment.UserName,
                    },
                    poolTemplate);

                await new IisAuditStore().AppendAsync(IisAuditStore.DefaultRoot, new IisAuditEntry
                {
                    EntityType = "Site",
                    EntityName = siteName,
                    Action = "Manual Deploy",
                    Details = label,
                    PerformedAtUtc = DateTimeOffset.UtcNow,
                    PerformedBy = Environment.UserName,
                });
            }

            _output.Info($"Manually deployed '{sourcePath}' to '{siteName}'{(_remoteMode ? " on the dev server" : "")}.");
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Deploy failed: {ex.Message}", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
            if (tempDir is not null && Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
}
