using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using PublishTool.Commands;
using PublishTool.Core;
using PublishTool.Core.Models;
using PublishTool.Core.Services;
using PublishTool.Core.Services.AppConfig;
using Wpf.Ui.Appearance;

namespace PublishTool.Gui;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
// Base type must match the ui:FluentWindow root element in MainWindow.xaml. Referenced fully
// qualified (not via a `using Wpf.Ui.Controls;`) because that namespace also has a MessageBox
// type, which would make the many MessageBox.Show(...) calls below ambiguous with System.Windows.
public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly GuiOutputSink _output;
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private readonly ObservableCollection<IisBinding> _iisBindings = new();
    private readonly ObservableCollection<AppConfigSettingRow> _appConfigSettings = new();
    private readonly HashSet<string> _currentProjectVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _currentProjectBranches = new(StringComparer.OrdinalIgnoreCase);
    private bool _isBusy;
    private bool _isExiting;
    private string? _lastSelectedProjectForForm;
    private GridLength _savedOutputColumnWidth = new(380);
    private readonly List<EventLogRowViewModel> _eventLogRows = new();
    private ICollectionView? _eventLogView;
    // Session-only cache: avoids re-prompting for a password on every Refresh within the same
    // app run, without writing anything to disk unless the user explicitly checked "Remember".
    private readonly Dictionary<string, string> _eventLogSessionPasswords = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow()
    {
        InitializeComponent();

        // An explicit user choice (set via the Settings tab) wins and stays fixed; otherwise
        // follow the OS theme live, same as before this setting existed.
        var startupSettings = AppSettings.Load(AppSettings.DefaultPath);
        if (startupSettings.Theme is "Light" or "Dark")
        {
            ApplicationThemeManager.Apply(startupSettings.Theme == "Dark" ? ApplicationTheme.Dark : ApplicationTheme.Light);
        }
        else
        {
            SystemThemeWatcher.Watch(this);
        }

        if (startupSettings.AccentColor is not null)
        {
            ApplyAccentColor(startupSettings.AccentColor);
        }

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "PublishTool",
            Visible = true,
            ContextMenuStrip = BuildTrayContextMenu(),
        };
        _notifyIcon.DoubleClick += (_, _) => RestoreFromTray();
        Closed += (_, _) => _notifyIcon.Dispose();
        Closing += MainWindow_Closing;

        IisBindingsDataGrid.ItemsSource = _iisBindings;
        AppConfigDataGrid.ItemsSource = _appConfigSettings;
        AppConfigTypeComboBox.ItemsSource = AppConfigProviderRegistry.All;
        ElevationInfoBar.IsOpen = !IsRunningAsAdministrator();
        AppVersionTextBlock.Text = $"PublishTool v{GetAppVersion()}";

        _output = new GuiOutputSink(OutputLogBox, StatusTextBlock, _notifyIcon);
        RefreshProjects();
        LoadSettingsIntoForm();

        Loaded += async (_, _) => await RefreshDependenciesAsync(showDialogIfMissing: true);
    }

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string GetAppVersion()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        // Trim to Major.Minor.Build -- the SDK always fills in a Revision (usually 0), which
        // isn't meaningful here and would just make "1.0.0" read as "1.0.0.0".
        return version is null ? "unknown" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        var resourceInfo = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        return resourceInfo is not null
            ? new System.Drawing.Icon(resourceInfo.Stream)
            : System.Drawing.SystemIcons.Application;
    }

    private System.Windows.Forms.ContextMenuStrip BuildTrayContextMenu()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open PublishTool", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitFromTray());
        return menu;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    // The X button hides the window instead of closing the app, so PublishTool keeps running
    // in the tray (e.g. so a background publish or the IIS monitoring stays available). Only
    // the tray menu's "Exit" (which sets _isExiting first) actually shuts the app down.
    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void ExitFromTray()
    {
        _isExiting = true;
        Close();
    }

    private const string StartupRegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "PublishTool";

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKeyPath, writable: false);
        return key?.GetValue(StartupValueName) is not null;
    }

    private static void SetStartupEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(StartupRegistryKeyPath);

        if (!enabled)
        {
            key.DeleteValue(StartupValueName, throwOnMissingValue: false);
            return;
        }

        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (exePath is not null)
        {
            key.SetValue(StartupValueName, $"\"{exePath}\"");
        }
    }

    private void StartOnStartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        SetStartupEnabled(StartOnStartupToggle.IsChecked == true);
    }

    private void LoadSettingsIntoForm()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        BuildsRootTextBox.Text = settings.BuildsRoot;
        DarkModeToggle.IsChecked = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
        StartOnStartupToggle.IsChecked = IsStartupEnabled();

        // Never re-display the saved API key itself -- the box always starts empty; leaving it
        // empty on Save keeps whatever's already saved, typing a new value replaces it.
        RemoteHostingUrlTextBox.Text = settings.RemoteHostingUrl ?? string.Empty;
        RemoteHostingStatusTextBlock.Text = string.Empty;

        var remoteHostingConfigured = !string.IsNullOrWhiteSpace(settings.RemoteHostingUrl);
        PublishToRemoteHostingToggle.IsEnabled = remoteHostingConfigured;
        PublishToRemoteHostingToggle.ToolTip = remoteHostingConfigured
            ? "Also upload this build to the configured Remote Build Hosting API."
            : "Configure a Remote Hosting URL in Settings first.";
        if (!remoteHostingConfigured)
        {
            PublishToRemoteHostingToggle.IsChecked = false;
        }
    }

    private async void DarkModeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        var isDark = DarkModeToggle.IsChecked == true;
        await RunAsync(new[] { "set-theme", "--value", isDark ? "Dark" : "Light" });
    }

    private async void AccentSwatch_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string presetName })
        {
            return;
        }

        var preset = AccentPresets.All.FirstOrDefault(p => p.Name == presetName);
        if (preset.Hex is null)
        {
            return;
        }

        await RunAsync(new[] { "set-accent-color", "--value", preset.Name });
        PromptRestartToApplyAccentColor();
    }

    // The accent color doesn't reliably repaint on an already-open window -- attempts to force
    // it (ApplicationThemeManager.Apply + WindowBackgroundManager.UpdateBackground) didn't work
    // in practice. Restarting is what actually works, since everything is created fresh with the
    // right accent at startup -- so just offer that instead of a half-working live update.
    private void PromptRestartToApplyAccentColor()
    {
        var result = MessageBox.Show(
            "Restart PublishTool now to apply the new accent color?",
            "PublishTool",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (exePath is not null)
        {
            Process.Start(exePath);
        }

        Application.Current.Shutdown();
    }

    private static void ApplyAccentColor(string hex)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        ApplicationAccentColorManager.Apply(color, ApplicationThemeManager.GetAppTheme());
    }

    private void BrowseBuildsRootButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog();
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            BuildsRootTextBox.Text = dialog.SelectedPath;
        }
    }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(BuildsRootTextBox.Text))
        {
            MessageBox.Show("Enter a builds root path.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await RunAsync(new[] { "set-builds-root", "--path", BuildsRootTextBox.Text });
        LoadSettingsIntoForm();
    }

    private void OpenBuildsRootButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        Directory.CreateDirectory(settings.BuildsRoot);
        Process.Start(new ProcessStartInfo { FileName = settings.BuildsRoot, UseShellExecute = true });
    }

    private void ExportProjectsButton_Click(object sender, RoutedEventArgs e)
    {
        var registry = new ProjectRegistry(ProjectRegistry.DefaultPath);
        if (registry.Projects.Count == 0)
        {
            MessageBox.Show("There are no registered projects to export yet.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var pickDialog = new ExportProjectsDialog(registry.Projects.Select(p => p.Name)) { Owner = this };
        if (pickDialog.ShowDialog() != true)
        {
            return;
        }

        var saveDialog = new SaveFileDialog
        {
            Filter = "PublishTool project export (*.ptproj.json)|*.ptproj.json|All files (*.*)|*.*",
            FileName = pickDialog.SelectedProjectNames.Count == 1
                ? $"{pickDialog.SelectedProjectNames[0]}.ptproj.json"
                : "PublishTool-projects.ptproj.json",
        };
        if (saveDialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var projects = pickDialog.SelectedProjectNames
                .Select(name => registry.Get(name))
                .Where(p => p is not null)
                .Select(p => p!);

            ProjectConfigPortability.Export(projects, saveDialog.FileName);
            _output.Info($"Exported {pickDialog.SelectedProjectNames.Count} project(s) to {saveDialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't export: {ex.Message}", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportProjectsButton_Click(object sender, RoutedEventArgs e)
    {
        var openDialog = new OpenFileDialog
        {
            Filter = "PublishTool project export (*.ptproj.json;*.json)|*.ptproj.json;*.json|All files (*.*)|*.*",
        };
        if (openDialog.ShowDialog() != true)
        {
            return;
        }

        ProjectConfigExportFile file;
        try
        {
            file = ProjectConfigPortability.Load(openDialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't read that file: {ex.Message}", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var registry = new ProjectRegistry(ProjectRegistry.DefaultPath);
        var preview = ProjectConfigPortability.Preview(file, registry);

        var previewDialog = new ImportProjectsDialog(file, preview) { Owner = this };
        if (previewDialog.ShowDialog() != true)
        {
            return;
        }

        ProjectConfigPortability.Import(file, registry, previewDialog.SelectedProjectNames);
        _output.Info($"Imported {previewDialog.SelectedProjectNames.Count} project(s) from {openDialog.FileName}");
        RefreshProjects();
    }

    private void SaveRemoteHostingButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        settings.RemoteHostingUrl = string.IsNullOrWhiteSpace(RemoteHostingUrlTextBox.Text) ? null : RemoteHostingUrlTextBox.Text.Trim();

        // Empty box keeps whatever's already saved -- the box never shows the real key back, so
        // "empty" can't be distinguished from "didn't mean to change it" any other way.
        if (!string.IsNullOrEmpty(RemoteHostingApiKeyBox.Password))
        {
            settings.RemoteHostingProtectedApiKey = SecretProtector.Protect(RemoteHostingApiKeyBox.Password, SecretProtector.RemoteHostingPurpose);
        }

        settings.Save(AppSettings.DefaultPath);
        RemoteHostingApiKeyBox.Password = string.Empty;
        RemoteHostingStatusTextBlock.Text = "Saved.";
        LoadSettingsIntoForm();
    }

    private async void TestRemoteHostingButton_Click(object sender, RoutedEventArgs e)
    {
        var url = RemoteHostingUrlTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Show("Enter a Remote Hosting URL first.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Test whatever's in the box right now if the user typed a new key without saving yet;
        // otherwise fall back to whatever's already saved, same as a real publish would use.
        string? apiKey;
        if (!string.IsNullOrEmpty(RemoteHostingApiKeyBox.Password))
        {
            apiKey = RemoteHostingApiKeyBox.Password;
        }
        else
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            apiKey = settings.RemoteHostingProtectedApiKey is null
                ? null
                : SecretProtector.TryUnprotect(settings.RemoteHostingProtectedApiKey, SecretProtector.RemoteHostingPurpose);
        }

        RemoteHostingStatusTextBlock.Text = "Testing...";
        TestRemoteHostingButton.IsEnabled = false;
        try
        {
            var ok = await new RemoteHostingClient().PingAsync(url, apiKey);
            RemoteHostingStatusTextBlock.Text = ok
                ? "Connected -- URL and API key are accepted."
                : "Couldn't connect -- check the URL and API key, and that the server is reachable.";
        }
        finally
        {
            TestRemoteHostingButton.IsEnabled = true;
        }
    }

    private void ToggleOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var isCurrentlyVisible = OutputPanel.Visibility == Visibility.Visible;
        if (isCurrentlyVisible)
        {
            // Remember whatever width the user last dragged the splitter to, so re-showing the
            // panel restores it instead of snapping back to the default. MinWidth has to drop to
            // 0 too -- otherwise the column still reserves 260px for an empty, Collapsed panel,
            // which is exactly the leftover-empty-space bug this is fixing.
            _savedOutputColumnWidth = OutputColumnDefinition.Width;
            OutputColumnDefinition.MinWidth = 0;
            OutputColumnDefinition.Width = new GridLength(0);
        }
        else
        {
            OutputColumnDefinition.MinWidth = 260;
            OutputColumnDefinition.Width = _savedOutputColumnWidth;
        }

        OutputPanel.Visibility = isCurrentlyVisible ? Visibility.Collapsed : Visibility.Visible;
        OutputSplitter.Visibility = isCurrentlyVisible ? Visibility.Collapsed : Visibility.Visible;
        ToggleOutputButton.Content = isCurrentlyVisible ? "Show output" : "Hide output";
    }

    private async void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged is a routed event that bubbles: selecting a row in a DataGrid/ListBox/
        // ComboBox nested inside a tab re-raises this same event on every ancestor Selector,
        // including this TabControl -- which would otherwise re-run this handler (and, for the
        // IIS tab, immediately refresh the grid and wipe out the row the user just selected).
        // Only react when the TabControl itself is what changed.
        if (e.Source != sender)
        {
            return;
        }

        if (MainTabControl.SelectedItem is TabItem { Header: "IIS" })
        {
            await RefreshIisStatusAsync();
        }
        else if (MainTabControl.SelectedItem is TabItem { Header: "Help" })
        {
            await RefreshDependenciesAsync();
        }
    }

    private async void RecheckDependenciesButton_Click(object sender, RoutedEventArgs e) => await RefreshDependenciesAsync();

    private async Task RefreshDependenciesAsync(bool showDialogIfMissing = false)
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        var results = await DependencyChecker.CheckAllAsync(settings.MsBuildPath);
        DependenciesDataGrid.ItemsSource = results;

        if (!showDialogIfMissing)
        {
            return;
        }

        var missing = results.Where(r => !r.IsAvailable).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        var message = "PublishTool is missing some dependencies it needs for full functionality:\n\n" +
                       string.Join("\n", missing.Select(m => $"• {m.Name}: {m.Details}")) +
                       "\n\nSee the Help tab for details.";

        MessageBox.Show(message, "PublishTool - Missing Dependencies", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void RefreshIisButton_Click(object sender, RoutedEventArgs e) => await RefreshIisStatusAsync();

    private async Task RefreshIisStatusAsync()
    {
        try
        {
            var manager = new IisSiteManager(_output);
            IisSitesDataGrid.ItemsSource = await manager.ListSitesAsync();
            IisAppPoolsDataGrid.ItemsSource = await manager.ListAppPoolsAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void StartSiteButton_Click(object sender, RoutedEventArgs e)
    {
        if (IisSitesDataGrid.SelectedItem is not IisSiteStatus site)
        {
            MessageBox.Show("Select a site first.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await RunAsync(new[] { "iis-start-site", "--name", site.Name });
        await RefreshIisStatusAsync();
    }

    private async void StopSiteButton_Click(object sender, RoutedEventArgs e)
    {
        if (IisSitesDataGrid.SelectedItem is not IisSiteStatus site)
        {
            MessageBox.Show("Select a site first.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await RunAsync(new[] { "iis-stop-site", "--name", site.Name });
        await RefreshIisStatusAsync();
    }

    private void BrowseSiteButton_Click(object sender, RoutedEventArgs e)
    {
        if (IisSitesDataGrid.SelectedItem is not IisSiteStatus site)
        {
            MessageBox.Show("Select a site first.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var url = BuildBrowseUrl(site.Bindings);
        if (url is null)
        {
            MessageBox.Show(
                $"Couldn't figure out a URL from this site's bindings ({site.Bindings}).",
                "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }

    // Bindings come from appcmd as comma-separated "protocol/ip:port:hostname" segments (see
    // IisSiteManager.FormatBinding). Picks the first one -- good enough for "open this site" --
    // and falls back to localhost when the binding's IP is "*" (all unassigned) with no hostname.
    private static string? BuildBrowseUrl(string bindingsRaw)
    {
        var firstBinding = bindingsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (firstBinding is null)
        {
            return null;
        }

        var slashIndex = firstBinding.IndexOf('/');
        if (slashIndex < 0)
        {
            return null;
        }

        var protocol = firstBinding[..slashIndex];
        var addressParts = firstBinding[(slashIndex + 1)..].Split(':', 3);
        if (addressParts.Length < 2)
        {
            return null;
        }

        var port = addressParts[1];
        var hostname = addressParts.Length > 2 && !string.IsNullOrWhiteSpace(addressParts[2])
            ? addressParts[2]
            : "localhost";

        var isDefaultPort = (protocol == "http" && port == "80") || (protocol == "https" && port == "443");
        return isDefaultPort ? $"{protocol}://{hostname}/" : $"{protocol}://{hostname}:{port}/";
    }

    private async void StartAppPoolButton_Click(object sender, RoutedEventArgs e)
    {
        if (IisAppPoolsDataGrid.SelectedItem is not IisAppPoolStatus pool)
        {
            MessageBox.Show("Select an application pool first.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await RunAsync(new[] { "iis-start-apppool", "--name", pool.Name });
        await RefreshIisStatusAsync();
    }

    private async void StopAppPoolButton_Click(object sender, RoutedEventArgs e)
    {
        if (IisAppPoolsDataGrid.SelectedItem is not IisAppPoolStatus pool)
        {
            MessageBox.Show("Select an application pool first.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await RunAsync(new[] { "iis-stop-apppool", "--name", pool.Name });
        await RefreshIisStatusAsync();
    }

    private async void RecycleAppPoolButton_Click(object sender, RoutedEventArgs e)
    {
        if (IisAppPoolsDataGrid.SelectedItem is not IisAppPoolStatus pool)
        {
            MessageBox.Show("Select an application pool first.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await RunAsync(new[] { "iis-recycle-apppool", "--name", pool.Name });
        await RefreshIisStatusAsync();
    }

    private void RefreshProjectsButton_Click(object sender, RoutedEventArgs e) => RefreshProjects();

    private void RefreshProjects()
    {
        var registry = new ProjectRegistry(ProjectRegistry.DefaultPath);

        var selectedInCombo = ProjectComboBox.SelectedItem as string;
        ProjectComboBox.ItemsSource = registry.Projects.Select(p => p.Name).ToList();
        if (selectedInCombo is not null)
        {
            ProjectComboBox.SelectedItem = selectedInCombo;
        }

        RegisteredProjectsListBox.ItemsSource = registry.Projects;

        // All projects, not just ones with Event Logs already enabled -- filtering the list down
        // used to leave this combo silently empty with no explanation for anyone who hadn't yet
        // turned the feature on for a project. LoadEventLogsForSelectedProjectAsync now says so
        // explicitly instead.
        var selectedInEventLogCombo = EventLogProjectComboBox.SelectedItem as string;
        EventLogProjectComboBox.ItemsSource = registry.Projects.Select(p => p.Name).ToList();
        if (selectedInEventLogCombo is not null)
        {
            EventLogProjectComboBox.SelectedItem = selectedInEventLogCombo;
        }
    }

    private async void ProjectComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var project = ProjectComboBox.SelectedItem as string;
        var changed = !string.Equals(project, _lastSelectedProjectForForm, StringComparison.Ordinal);
        _lastSelectedProjectForForm = project;

        LoadVersionsForSelectedProject();

        // Only reset the form when the project actually changed -- ProjectComboBox gets
        // reselected (to the same value) after every command via RefreshProjects(), and that
        // shouldn't wipe out release notes the user is still editing.
        if (changed)
        {
            VersionComboBox.Text = string.Empty;
            VersionOverwriteHintTextBlock.Visibility = Visibility.Collapsed;
            FeaturesEditor.Clear();
            FixesEditor.Clear();
            OtherUpdatesEditor.Clear();
            BacklogItemsEditor.Clear();
            LoadAppConfigForSelectedProject();
        }

        await LoadGitBranchesForSelectedProjectAsync();
    }

    /// <summary>Shows/hides the App Config accordion for the selected project and, if it uses
    /// app config, seeds the grid from the live config file on disk (the starting point before
    /// the user picks a specific already-published version, which would show that version's
    /// saved settings instead -- see VersionComboBox_SelectionChanged).</summary>
    private void LoadAppConfigForSelectedProject()
    {
        _appConfigSettings.Clear();

        var projectName = ProjectComboBox.SelectedItem as string;
        var project = string.IsNullOrWhiteSpace(projectName) ? null : new ProjectRegistry(ProjectRegistry.DefaultPath).Get(projectName);

        if (project is not { UseAppConfig: true } || AppConfigProviderRegistry.Get(project.AppConfigType) is not { } provider)
        {
            AppConfigExpander.Visibility = Visibility.Collapsed;
            return;
        }

        AppConfigExpander.Visibility = Visibility.Visible;
        AppConfigDescriptionTextBlock.Text = $"Editing {provider.DisplayName} at {project.AppConfigPath}";

        if (string.IsNullOrWhiteSpace(project.AppConfigPath) || !File.Exists(project.AppConfigPath))
        {
            return;
        }

        try
        {
            foreach (var (key, value) in provider.ReadSettings(project.AppConfigPath))
            {
                _appConfigSettings.Add(new AppConfigSettingRow { Key = key, Value = value });
            }
        }
        catch (Exception ex)
        {
            _output.Warn($"Couldn't read app config: {ex.Message}");
        }
    }

    private void AddAppConfigSettingButton_Click(object sender, RoutedEventArgs e) =>
        _appConfigSettings.Add(new AppConfigSettingRow());

    private void RemoveAppConfigSettingButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppConfigDataGrid.SelectedItem is AppConfigSettingRow row)
        {
            _appConfigSettings.Remove(row);
        }
    }

    private void LoadVersionsForSelectedProject()
    {
        _currentProjectVersions.Clear();

        var project = ProjectComboBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(project))
        {
            VersionComboBox.ItemsSource = null;
            return;
        }

        var settings = AppSettings.Load(AppSettings.DefaultPath);
        var buildRepository = new BuildRepository();
        var versions = buildRepository.ListBuilds(settings.BuildsRoot, project)
            .Select(b => b.Version)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var version in versions)
        {
            _currentProjectVersions.Add(version);
        }

        VersionComboBox.ItemsSource = versions;
    }

    private async Task LoadGitBranchesForSelectedProjectAsync()
    {
        _currentProjectBranches.Clear();
        GitBranchAutoSuggestBox.OriginalItemsSource = Array.Empty<string>();
        GitBranchAutoSuggestBox.Text = string.Empty;
        GitBranchAutoSuggestBox.IsEnabled = false;

        var projectName = ProjectComboBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(projectName))
        {
            return;
        }

        var registry = new ProjectRegistry(ProjectRegistry.DefaultPath);
        var project = registry.Get(projectName);
        if (project is null)
        {
            return;
        }

        GitBranchInfo? info;
        try
        {
            info = await new GitService(_output).ListBranchesAsync(project.CsprojPath);
        }
        catch
        {
            // git isn't installed, or something else went wrong probing the repo -- leave the
            // branch picker empty/disabled rather than surfacing this as a hard error.
            info = null;
        }

        if (info is null)
        {
            return;
        }

        foreach (var branch in info.Branches)
        {
            _currentProjectBranches.Add(branch);
        }

        GitBranchAutoSuggestBox.OriginalItemsSource = info.Branches.ToList();
        GitBranchAutoSuggestBox.Text = info.CurrentBranch;
        GitBranchAutoSuggestBox.IsEnabled = true;
    }

    /// <summary>
    /// Switching the branch picker doesn't touch the working tree by itself -- publish checks
    /// out whatever's selected right before building, but until then the App Config panel would
    /// otherwise keep showing the previously-checked-out branch's file. This checks out the
    /// selected branch immediately, so App Config (and anything else read from disk) reflects it.
    /// </summary>
    private async void CheckoutBranchButton_Click(object sender, RoutedEventArgs e)
    {
        var project = ProjectComboBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(project))
        {
            MessageBox.Show("Select a project first.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var branch = GitBranchAutoSuggestBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(branch) || !_currentProjectBranches.Contains(branch))
        {
            MessageBox.Show(
                "Pick a branch from the search list first.",
                "PublishTool",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var projectConfig = new ProjectRegistry(ProjectRegistry.DefaultPath).Get(project);
        if (projectConfig is null)
        {
            return;
        }

        // Called directly (not through RunAsync/CommandLineFactory) because handling a checkout
        // conflict needs the structured GitCheckoutConflictException -- the CLI command only ever
        // surfaces a logged error string, which isn't enough to drive the resolution dialog below.
        if (_isBusy)
        {
            return;
        }

        _isBusy = true;
        SetBusy(true);
        try
        {
            await CheckoutBranchAsync(projectConfig, branch);
        }
        finally
        {
            _isBusy = false;
            SetBusy(false);
        }

        // The working tree may have just changed on disk -- reload whatever reads from it.
        LoadAppConfigForSelectedProject();
        await LoadGitBranchesForSelectedProjectAsync();
    }

    private async Task CheckoutBranchAsync(ProjectConfig project, string branch)
    {
        var git = new GitService(_output);

        // No-op if we're already there -- CheckoutAsync would also catch this, but checking here
        // first avoids bothering the user with the uncommitted-changes prompt below for a checkout
        // that isn't actually going to change anything.
        var currentBranch = await git.GetCurrentBranchAsync(project.CsprojPath);
        if (string.Equals(currentBranch, branch, StringComparison.OrdinalIgnoreCase))
        {
            _output.Info($"Already on branch '{branch}'.");
            return;
        }

        // Proactive check: git doesn't always BLOCK a checkout just because there are uncommitted
        // changes -- if the target branch doesn't touch the same files, they silently carry over
        // onto the new branch instead. Ask first rather than letting that happen invisibly.
        IReadOnlyList<string> uncommittedFiles;
        try
        {
            uncommittedFiles = await git.GetUncommittedChangesAsync(project.CsprojPath);
        }
        catch (Exception ex)
        {
            _output.Warn($"Couldn't check for uncommitted changes: {ex.Message}");
            uncommittedFiles = Array.Empty<string>();
        }

        if (uncommittedFiles.Count > 0)
        {
            var dialog = new GitConflictDialog(branch, uncommittedFiles, isBlocking: false) { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                _output.Info("Checkout cancelled.");
                return;
            }

            if (dialog.Resolution != GitConflictResolution.CheckoutAnyway &&
                !await ApplyGitResolutionAsync(git, project, branch, dialog.Resolution, uncommittedFiles, dialog.CommitMessage))
            {
                return;
            }

            // else CheckoutAnyway: fall through and let the changes carry over, same as if the
            // user had run "git checkout" themselves with nothing staged.
        }

        try
        {
            await git.CheckoutAsync(project.CsprojPath, branch);
            _output.Stage("Checkout complete.");
            return;
        }
        catch (GitCheckoutConflictException conflict)
        {
            // Even after the prompt above (or if they chose "Checkout anyway"), git can still
            // refuse for files that actually conflict with the target branch -- handle that the
            // same way, just with the "blocked" framing instead of the proactive one.
            var dialog = new GitConflictDialog(branch, conflict.ConflictingFiles, isBlocking: true) { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                _output.Info("Checkout cancelled -- resolve the conflicting files yourself (e.g. in your IDE), then try again.");
                return;
            }

            if (!await ApplyGitResolutionAsync(git, project, branch, dialog.Resolution, conflict.ConflictingFiles, dialog.CommitMessage))
            {
                return;
            }

            try
            {
                await git.CheckoutAsync(project.CsprojPath, branch);
                _output.Stage("Checkout complete.");
            }
            catch (Exception ex)
            {
                _output.Error(ex.Message);
            }
        }
        catch (Exception ex)
        {
            _output.Error(ex.Message);
        }
    }

    /// <summary>Applies a Discard/Stash/Commit resolution chosen in <see cref="GitConflictDialog"/>
    /// to exactly the given files. Returns false (having already logged the error) on failure, so
    /// callers can bail out of whatever checkout attempt was waiting on it.</summary>
    private async Task<bool> ApplyGitResolutionAsync(
        GitService git, ProjectConfig project, string branch, GitConflictResolution resolution, IReadOnlyList<string> files, string commitMessage)
    {
        try
        {
            switch (resolution)
            {
                case GitConflictResolution.Discard:
                    await git.DiscardChangesAsync(project.CsprojPath, files);
                    break;
                case GitConflictResolution.Stash:
                    await git.StashChangesAsync(project.CsprojPath, files, $"PublishTool: before checkout to {branch}");
                    break;
                case GitConflictResolution.Commit:
                    await git.CommitChangesAsync(project.CsprojPath, files, commitMessage);
                    break;
            }

            return true;
        }
        catch (Exception ex)
        {
            _output.Error(ex.Message);
            return false;
        }
    }

    private void VersionComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateVersionOverwriteHint();

        if (VersionComboBox.SelectedItem is not string version)
        {
            return;
        }

        var project = ProjectComboBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(project))
        {
            return;
        }

        FeaturesEditor.Clear();
        FixesEditor.Clear();
        OtherUpdatesEditor.Clear();
        BacklogItemsEditor.Clear();
        LoadAppConfigForSelectedProject();

        var settings = AppSettings.Load(AppSettings.DefaultPath);
        var buildRepository = new BuildRepository();
        var existing = buildRepository.FindBuild(settings.BuildsRoot, project, version);

        ApplySavedAppConfigForSelectedVersion(existing);

        if (existing?.Manifest.ReleaseNotesPath is not { } notesPath || !File.Exists(notesPath))
        {
            return;
        }

        var entry = ReleaseNotesFormatter.Parse(File.ReadAllText(notesPath));
        if (entry is not null)
        {
            foreach (var item in entry.Features) { FeaturesEditor.Items.Add(item); }
            foreach (var item in entry.Fixes) { FixesEditor.Items.Add(item); }
            foreach (var item in entry.OtherUpdates) { OtherUpdatesEditor.Items.Add(item); }
            foreach (var item in entry.BacklogItems) { BacklogItemsEditor.Items.Add(item); }
        }
    }

    /// <summary>Restores the app config settings this specific build was published with, if any
    /// -- overriding whatever LoadAppConfigForSelectedProject seeded from the live config file,
    /// so re-selecting a published version shows exactly what was published for it.</summary>
    private void ApplySavedAppConfigForSelectedVersion(ExistingBuild? existing)
    {
        if (existing?.Manifest.AppConfigSettings is not { Count: > 0 } saved)
        {
            return;
        }

        _appConfigSettings.Clear();
        foreach (var (key, value) in saved)
        {
            _appConfigSettings.Add(new AppConfigSettingRow { Key = key, Value = value });
        }
    }

    private void VersionComboBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        UpdateVersionOverwriteHint();

    private void UpdateVersionOverwriteHint()
    {
        var text = VersionComboBox.Text;
        VersionOverwriteHintTextBlock.Visibility =
            !string.IsNullOrWhiteSpace(text) && _currentProjectVersions.Contains(text)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private async void PublishButton_Click(object sender, RoutedEventArgs e)
    {
        var project = ProjectComboBox.SelectedItem as string;
        var version = VersionComboBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(version))
        {
            MessageBox.Show(
                "Select a project and fill in a version.",
                "PublishTool",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (_currentProjectVersions.Contains(version))
        {
            var confirm = MessageBox.Show(
                $"Version '{version}' already exists for '{project}'. Publishing will overwrite its build and release notes. Continue?",
                "PublishTool",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }
        }

        var branch = GitBranchAutoSuggestBox.Text?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(branch) && !_currentProjectBranches.Contains(branch))
        {
            MessageBox.Show(
                $"'{branch}' isn't a branch on this project's repo. Pick one from the search list, or clear the field to build the current branch as-is.",
                "PublishTool",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var args = new List<string>
        {
            "publish",
            "--project", project,
            "--version", version,
        };

        if (!string.IsNullOrWhiteSpace(branch))
        {
            args.Add("--git-branch");
            args.Add(branch);
        }

        foreach (var item in FeaturesEditor.Items) { args.Add("--feature"); args.Add(item); }
        foreach (var item in FixesEditor.Items) { args.Add("--fix"); args.Add(item); }
        foreach (var item in OtherUpdatesEditor.Items) { args.Add("--other-update"); args.Add(item); }
        foreach (var item in BacklogItemsEditor.Items) { args.Add("--backlog-item"); args.Add(item); }

        if (MarkAsLatestToggle.IsChecked == true)
        {
            args.Add("--mark-latest");
        }

        if (PublishToRemoteHostingToggle.IsChecked == true)
        {
            args.Add("--publish-to-remote-hosting");
        }

        if (AppConfigExpander.Visibility == Visibility.Visible)
        {
            foreach (var row in _appConfigSettings)
            {
                if (string.IsNullOrWhiteSpace(row.Key))
                {
                    continue;
                }

                args.Add("--app-config-setting");
                args.Add($"{row.Key}={row.Value}");
            }
        }

        await RunAsync(args.ToArray());
        LoadVersionsForSelectedProject();
    }

    private void BrowseCsproj_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Project files (*.csproj)|*.csproj|All files (*.*)|*.*" };
        if (dialog.ShowDialog() == true)
        {
            NewProjectCsprojTextBox.Text = dialog.FileName;
        }
    }

    private void BrowsePubxml_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Publish profiles (*.pubxml)|*.pubxml|All files (*.*)|*.*" };
        if (dialog.ShowDialog() == true)
        {
            // MSBuild's PublishProfile property takes the profile name without the extension.
            NewProjectPubxmlTextBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
        }
    }

    private void BrowseAssemblyInfo_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "C# files (*.cs)|*.cs|All files (*.*)|*.*" };
        if (dialog.ShowDialog() == true)
        {
            NewProjectAssemblyInfoTextBox.Text = dialog.FileName;
        }
    }

    private void BrowseIisHost_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog();
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            NewProjectIisHostTextBox.Text = dialog.SelectedPath;
        }
    }

    private void AutoCreateIisSiteToggle_Toggled(object sender, RoutedEventArgs e)
    {
        IisBindingsPanel.Visibility = AutoCreateIisSiteToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UseAppConfigToggle_Toggled(object sender, RoutedEventArgs e)
    {
        var isOn = UseAppConfigToggle.IsChecked == true;
        AppConfigPanel.Visibility = isOn ? Visibility.Visible : Visibility.Collapsed;
        if (isOn && AppConfigTypeComboBox.SelectedItem is null && AppConfigTypeComboBox.Items.Count > 0)
        {
            AppConfigTypeComboBox.SelectedIndex = 0;
        }
    }

    private void BrowseAppConfigPath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Config files (*.config)|*.config|All files (*.*)|*.*" };
        if (dialog.ShowDialog() == true)
        {
            AppConfigPathTextBox.Text = dialog.FileName;
        }
    }

    private void UseEventLogToggle_Toggled(object sender, RoutedEventArgs e)
    {
        var isOn = UseEventLogToggle.IsChecked == true;
        EventLogPanel.Visibility = isOn ? Visibility.Visible : Visibility.Collapsed;
        if (isOn && string.IsNullOrWhiteSpace(EventLogNameTextBox.Text))
        {
            EventLogNameTextBox.Text = "Application";
        }
    }

    private void AddBindingButton_Click(object sender, RoutedEventArgs e)
    {
        _iisBindings.Add(new IisBinding { Protocol = "http", IpAddress = "*", Port = 80, HostName = null });
    }

    private void RemoveBindingButton_Click(object sender, RoutedEventArgs e)
    {
        if (IisBindingsDataGrid.SelectedItem is IisBinding binding)
        {
            _iisBindings.Remove(binding);
        }
    }

    private async void SaveProjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NewProjectNameTextBox.Text) ||
            string.IsNullOrWhiteSpace(NewProjectCsprojTextBox.Text) ||
            string.IsNullOrWhiteSpace(NewProjectPubxmlTextBox.Text) ||
            string.IsNullOrWhiteSpace(NewProjectIisHostTextBox.Text))
        {
            MessageBox.Show(
                "Name, .csproj path, publish profile, and IIS host folder are required.",
                "PublishTool",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var autoCreateIisSite = AutoCreateIisSiteToggle.IsChecked == true;
        if (autoCreateIisSite && _iisBindings.Count == 0)
        {
            MessageBox.Show(
                "Auto-create IIS site is on but no bindings were added. Add at least one binding, or turn the toggle off.",
                "PublishTool",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var useAppConfig = UseAppConfigToggle.IsChecked == true;
        if (useAppConfig && (AppConfigTypeComboBox.SelectedItem is not IAppConfigProvider || string.IsNullOrWhiteSpace(AppConfigPathTextBox.Text)))
        {
            MessageBox.Show(
                "App config editing is on but the config type or file path is missing. Fill both in, or turn the toggle off.",
                "PublishTool",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var useEventLog = UseEventLogToggle.IsChecked == true;
        if (useEventLog && string.IsNullOrWhiteSpace(EventLogFilterValueTextBox.Text))
        {
            MessageBox.Show(
                "Event Logs is on but no Source name or message text filter was entered. Fill it in, or turn the toggle off.",
                "PublishTool",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var args = new List<string>
        {
            "add-project",
            "--name", NewProjectNameTextBox.Text,
            "--csproj", NewProjectCsprojTextBox.Text,
            "--pubxml", NewProjectPubxmlTextBox.Text,
            "--iis-host", NewProjectIisHostTextBox.Text,
        };

        if (!string.IsNullOrWhiteSpace(NewProjectIdTextBox.Text))
        {
            args.Add("--project-id");
            args.Add(NewProjectIdTextBox.Text);
        }

        if (!string.IsNullOrWhiteSpace(NewProjectAssemblyInfoTextBox.Text))
        {
            args.Add("--assembly-info");
            args.Add(NewProjectAssemblyInfoTextBox.Text);
        }

        if (!string.IsNullOrWhiteSpace(NewProjectExtraTargetsTextBox.Text))
        {
            args.Add("--extra-publish-targets");
            args.Add(NewProjectExtraTargetsTextBox.Text);
        }

        if (autoCreateIisSite)
        {
            args.Add("--auto-create-iis-site");
            foreach (var binding in _iisBindings)
            {
                args.Add("--iis-binding");
                args.Add($"{binding.Protocol}:{binding.IpAddress}:{binding.Port}:{binding.HostName}");
            }
        }

        if (SdkStyleProjectToggle.IsChecked == true)
        {
            args.Add("--sdk-style-project");
        }

        args.Add("--list-in-hosting");
        args.Add(ListInHostingToggle.IsChecked == true ? "true" : "false");

        if (useAppConfig && AppConfigTypeComboBox.SelectedItem is IAppConfigProvider provider)
        {
            args.Add("--app-config-type");
            args.Add(provider.TypeName);
            args.Add("--app-config-path");
            args.Add(AppConfigPathTextBox.Text);
        }

        if (useEventLog)
        {
            args.Add("--enable-event-log");
            args.Add("--event-log-name");
            args.Add(string.IsNullOrWhiteSpace(EventLogNameTextBox.Text) ? "Application" : EventLogNameTextBox.Text);

            var filterType = (EventLogFilterTypeComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? EventLogFilterTypes.Source;
            args.Add("--event-log-filter-type");
            args.Add(filterType);

            args.Add("--event-log-filter-value");
            args.Add(EventLogFilterValueTextBox.Text);

            if (!string.IsNullOrWhiteSpace(EventLogMachineTextBox.Text))
            {
                args.Add("--event-log-machine");
                args.Add(EventLogMachineTextBox.Text);
            }

            if (!string.IsNullOrWhiteSpace(EventLogUsernameTextBox.Text))
            {
                args.Add("--event-log-username");
                args.Add(EventLogUsernameTextBox.Text);
            }
        }

        await RunAsync(args.ToArray());
    }

    private async void RemoveProjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (RegisteredProjectsListBox.SelectedItem is not ProjectConfig project)
        {
            MessageBox.Show(
                "Select a project in the list below first.",
                "PublishTool",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            $"Remove '{project.Name}' from PublishTool? This only unregisters it — archived builds and the IIS host folder are untouched.",
            "PublishTool",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        await RunAsync(new[] { "remove-project", "--name", project.Name });
        NewProjectButton_Click(sender, e);
    }

    private void NewProjectButton_Click(object sender, RoutedEventArgs e)
    {
        NewProjectNameTextBox.Clear();
        NewProjectIdTextBox.Clear();
        NewProjectCsprojTextBox.Clear();
        NewProjectPubxmlTextBox.Clear();
        NewProjectAssemblyInfoTextBox.Clear();
        NewProjectIisHostTextBox.Clear();
        NewProjectExtraTargetsTextBox.Clear();
        AutoCreateIisSiteToggle.IsChecked = false;
        IisBindingsPanel.Visibility = Visibility.Collapsed;
        _iisBindings.Clear();
        SdkStyleProjectToggle.IsChecked = false;
        ListInHostingToggle.IsChecked = true;
        UseAppConfigToggle.IsChecked = false;
        AppConfigPanel.Visibility = Visibility.Collapsed;
        AppConfigTypeComboBox.SelectedItem = null;
        AppConfigPathTextBox.Clear();
        UseEventLogToggle.IsChecked = false;
        EventLogPanel.Visibility = Visibility.Collapsed;
        EventLogNameTextBox.Clear();
        EventLogFilterTypeComboBox.SelectedIndex = 0;
        EventLogFilterValueTextBox.Clear();
        EventLogMachineTextBox.Clear();
        EventLogUsernameTextBox.Clear();
        RegisteredProjectsListBox.SelectedItem = null;
    }

    private void RegisteredProjectsListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (RegisteredProjectsListBox.SelectedItem is not ProjectConfig project)
        {
            return;
        }

        NewProjectNameTextBox.Text = project.Name;
        NewProjectIdTextBox.Text = project.ProjectId ?? string.Empty;
        NewProjectCsprojTextBox.Text = project.CsprojPath;
        NewProjectPubxmlTextBox.Text = project.PubxmlName;
        NewProjectAssemblyInfoTextBox.Text = project.AssemblyInfoPath ?? string.Empty;
        NewProjectIisHostTextBox.Text = project.IisHostPath;
        NewProjectExtraTargetsTextBox.Text = project.ExtraPublishTargets ?? string.Empty;
        SdkStyleProjectToggle.IsChecked = project.SdkStyleProject;
        ListInHostingToggle.IsChecked = project.ListInHosting;

        AutoCreateIisSiteToggle.IsChecked = project.AutoCreateIisSite;
        IisBindingsPanel.Visibility = project.AutoCreateIisSite ? Visibility.Visible : Visibility.Collapsed;
        _iisBindings.Clear();
        foreach (var binding in project.IisBindings)
        {
            _iisBindings.Add(new IisBinding
            {
                Protocol = binding.Protocol,
                IpAddress = binding.IpAddress,
                Port = binding.Port,
                HostName = binding.HostName,
            });
        }

        UseAppConfigToggle.IsChecked = project.UseAppConfig;
        AppConfigPanel.Visibility = project.UseAppConfig ? Visibility.Visible : Visibility.Collapsed;
        AppConfigTypeComboBox.SelectedItem = AppConfigProviderRegistry.Get(project.AppConfigType);
        AppConfigPathTextBox.Text = project.AppConfigPath ?? string.Empty;

        UseEventLogToggle.IsChecked = project.UseEventLog;
        EventLogPanel.Visibility = project.UseEventLog ? Visibility.Visible : Visibility.Collapsed;
        EventLogNameTextBox.Text = project.EventLogName ?? "Application";
        EventLogFilterTypeComboBox.SelectedIndex = string.Equals(project.EventLogFilterType, EventLogFilterTypes.MessageContains, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        EventLogFilterValueTextBox.Text = project.EventLogFilterValue ?? string.Empty;
        EventLogMachineTextBox.Text = project.EventLogMachineName ?? string.Empty;
        EventLogUsernameTextBox.Text = project.EventLogUsername ?? string.Empty;
    }

    private async void EventLogProjectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        await LoadEventLogsForSelectedProjectAsync();

    private async void RefreshEventLogButton_Click(object sender, RoutedEventArgs e) =>
        await LoadEventLogsForSelectedProjectAsync();

    private async Task LoadEventLogsForSelectedProjectAsync()
    {
        var projectName = EventLogProjectComboBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(projectName))
        {
            _eventLogRows.Clear();
            EventLogDataGrid.ItemsSource = null;
            EventLogStatusTextBlock.Text = string.Empty;
            return;
        }

        var project = new ProjectRegistry(ProjectRegistry.DefaultPath).Get(projectName);
        if (project is null)
        {
            return;
        }

        if (!project.UseEventLog)
        {
            _eventLogRows.Clear();
            EventLogDataGrid.ItemsSource = null;
            EventLogStatusTextBlock.Text =
                $"Event Logs isn't enabled for '{project.Name}' -- turn on \"Enable Event Logs tab for this project\" " +
                "in the Add Project tab first, then save.";
            return;
        }

        EventLogStatusTextBlock.Text = "Loading...";
        RefreshEventLogButton.IsEnabled = false;

        try
        {
            string? password = null;
            var needsPassword = !string.IsNullOrWhiteSpace(project.EventLogMachineName) && !string.IsNullOrWhiteSpace(project.EventLogUsername);
            if (needsPassword)
            {
                password = await ResolveEventLogPasswordAsync(project);
                if (password is null)
                {
                    EventLogStatusTextBlock.Text = "Cancelled -- a password is required to connect to that machine.";
                    return;
                }
            }

            var options = new EventLogQueryOptions
            {
                LogName = string.IsNullOrWhiteSpace(project.EventLogName) ? "Application" : project.EventLogName,
                MachineName = string.IsNullOrWhiteSpace(project.EventLogMachineName) ? null : project.EventLogMachineName,
                Username = string.IsNullOrWhiteSpace(project.EventLogUsername) ? null : project.EventLogUsername,
                Password = password,
                FilterType = project.EventLogFilterType ?? EventLogFilterTypes.Source,
                FilterValue = project.EventLogFilterValue,
            };

            var reader = new EventLogReaderService();
            // Reading (especially remote) is blocking I/O -- keep it off the UI thread, same
            // pattern as the other Core services this GUI calls directly.
            var records = await Task.Run(() => reader.GetRecent(options));

            _eventLogRows.Clear();
            _eventLogRows.AddRange(records.Select(r => new EventLogRowViewModel(r)));

            PopulateEventLogMethodFilter();

            _eventLogView = CollectionViewSource.GetDefaultView(_eventLogRows);
            EventLogDataGrid.ItemsSource = _eventLogView;
            ApplyEventLogFilter();
        }
        catch (Exception ex)
        {
            _eventLogRows.Clear();
            EventLogDataGrid.ItemsSource = null;
            EventLogStatusTextBlock.Text = $"Failed to read event log: {ex.Message}";
        }
        finally
        {
            RefreshEventLogButton.IsEnabled = true;
        }
    }

    /// <summary>Resolves the password needed to query a remote event log with explicit
    /// credentials, checking the in-memory session cache and any saved (DPAPI-protected) password
    /// before falling back to prompting. Returns null if the user cancels the prompt.</summary>
    private async Task<string?> ResolveEventLogPasswordAsync(ProjectConfig project)
    {
        if (_eventLogSessionPasswords.TryGetValue(project.Name, out var cached))
        {
            return cached;
        }

        if (project.EventLogProtectedPassword is not null)
        {
            var unprotected = SecretProtector.TryUnprotect(project.EventLogProtectedPassword);
            if (unprotected is not null)
            {
                _eventLogSessionPasswords[project.Name] = unprotected;
                return unprotected;
            }

            _output.Warn($"Saved password for '{project.Name}' couldn't be decrypted (saved by a different Windows user or machine?) -- re-enter it.");
        }

        var dialog = new CredentialPromptDialog(project.EventLogMachineName!, project.EventLogUsername!) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        _eventLogSessionPasswords[project.Name] = dialog.Password;

        if (dialog.RememberPassword)
        {
            project.EventLogProtectedPassword = SecretProtector.Protect(dialog.Password);
            new ProjectRegistry(ProjectRegistry.DefaultPath).AddOrUpdate(project);
        }

        return await Task.FromResult(dialog.Password);
    }

    /// <summary>Rebuilds the Method filter's options from whatever method names were actually
    /// extracted from the just-loaded entries -- there's no fixed list, it depends entirely on
    /// what's in the log. Keeps the current selection if it's still a valid option.</summary>
    private void PopulateEventLogMethodFilter()
    {
        var methods = _eventLogRows
            .Select(r => r.MethodName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var previouslySelected = EventLogMethodFilterComboBox.SelectedItem as string;

        var items = new List<string> { "All methods" };
        items.AddRange(methods!);
        EventLogMethodFilterComboBox.ItemsSource = items;
        EventLogMethodFilterComboBox.SelectedItem = previouslySelected is not null && items.Contains(previouslySelected)
            ? previouslySelected
            : "All methods";
    }

    private void EventLogSearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyEventLogFilter();

    private void EventLogLevelFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyEventLogFilter();

    private void EventLogMethodFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyEventLogFilter();

    private void EventLogTypeFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyEventLogFilter();

    private void ApplyEventLogFilter()
    {
        if (_eventLogView is null)
        {
            return;
        }

        var search = EventLogSearchTextBox.Text?.Trim() ?? string.Empty;
        var levelFilter = (EventLogLevelFilterComboBox.SelectedItem as ComboBoxItem)?.Content as string;
        var methodFilter = EventLogMethodFilterComboBox.SelectedItem as string;
        var typeFilter = (EventLogTypeFilterComboBox.SelectedItem as ComboBoxItem)?.Content as string;

        _eventLogView.Filter = item =>
            item is EventLogRowViewModel row &&
            (string.IsNullOrWhiteSpace(levelFilter) || levelFilter == "All levels" || string.Equals(row.Level, levelFilter, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(methodFilter) || methodFilter == "All methods" || string.Equals(row.MethodName, methodFilter, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(typeFilter) || typeFilter == "All types" || string.Equals(row.MessageType, typeFilter, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(search) || row.MatchesSearch(search));

        var visibleCount = _eventLogRows.Count(row => _eventLogView.Filter(row));
        EventLogStatusTextBlock.Text = _eventLogRows.Count == 0
            ? "No entries found."
            : $"Showing {visibleCount} of {_eventLogRows.Count} entries.";
    }

    private void EventLogDataGrid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Only react to an actual click landing on a data row -- not the column headers, not an
        // empty area below the last row, and not keyboard-driven selection changes (arrow keys),
        // which a plain SelectionChanged handler would also (annoyingly) trigger on.
        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item is not EventLogRowViewModel entry)
        {
            return;
        }

        // This handler runs during the tunneling (Preview) phase, before DataGrid's own internal
        // row-selection handling (which happens on the way back up, and may still hold mouse
        // capture at this point) has finished. Showing a modal window synchronously from inside
        // an input event handler is a known WPF hazard -- deferring to the dispatcher queue lets
        // the current input event finish completely first. Also wrapped in try/catch: if
        // something about this specific interaction still throws, show it instead of letting an
        // unhandled exception through (the App-level handler would catch it either way, but this
        // keeps the error message specific to what the user was actually doing).
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                new EventLogDetailDialog(entry) { Owner = this }.ShowDialog();
            }
            catch (Exception ex)
            {
                _output.Error($"Couldn't open the event log entry details: {ex.Message}");
            }
        });
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void ExportEventLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (_eventLogView is null)
        {
            return;
        }

        var dialog = new SaveFileDialog { Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*", FileName = "EventLogs.csv" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("Time,Level,Source,EventId,Message");
        foreach (var row in _eventLogView.Cast<EventLogRowViewModel>())
        {
            sb.AppendLine(string.Join(',', CsvField(row.TimeDisplay), CsvField(row.Level), CsvField(row.Source), CsvField(row.EventId.ToString()), CsvField(row.FullMessage)));
        }

        File.WriteAllText(dialog.FileName, sb.ToString());
        _output.Info($"Exported event logs to {dialog.FileName}");
    }

    private static string CsvField(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private async void RunCommandButton_Click(object sender, RoutedEventArgs e) => await RunCommandBoxAsync();

    private async void CommandInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await RunCommandBoxAsync();
        }
    }

    private async Task RunCommandBoxAsync()
    {
        var input = CommandInputBox.Text;
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        await RunAsync(CommandLineTokenizer.Tokenize(input));
    }

    private async Task RunAsync(string[] args)
    {
        if (_isBusy)
        {
            return;
        }

        _isBusy = true;
        SetBusy(true);
        _output.Info($"> {string.Join(' ', args)}");

        try
        {
            var rootCommand = CommandLineFactory.Create(_output);
            var parseResult = rootCommand.Parse(args);
            await parseResult.InvokeAsync();
        }
        finally
        {
            _isBusy = false;
            SetBusy(false);
            RefreshProjects();
        }
    }

    private void SetBusy(bool busy)
    {
        PublishProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusTextBlock.Text = busy ? "Working..." : "Idle";

        PublishButton.IsEnabled = !busy;
        SaveProjectButton.IsEnabled = !busy;
        RemoveProjectButton.IsEnabled = !busy;
        RunCommandButton.IsEnabled = !busy;
        RefreshIisButton.IsEnabled = !busy;
        CheckoutBranchButton.IsEnabled = !busy;
    }
}
