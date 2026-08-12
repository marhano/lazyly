using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    private void ToggleOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var isCurrentlyVisible = OutputPanel.Visibility == Visibility.Visible;
        if (isCurrentlyVisible)
        {
            // Remember whatever width the user last dragged the splitter to, so re-showing the
            // panel restores it instead of snapping back to the default.
            _savedOutputColumnWidth = OutputColumnDefinition.Width;
            OutputColumnDefinition.Width = new GridLength(0);
        }
        else
        {
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
    }

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
