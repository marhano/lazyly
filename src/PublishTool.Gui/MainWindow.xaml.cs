using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
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
    private readonly ObservableCollection<AppConfigSettingRow> _appConfigSettings = new();
    private readonly HashSet<string> _currentProjectVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _currentProjectBranches = new(StringComparer.OrdinalIgnoreCase);
    private bool _isBusy;
    private bool _isExiting;
    // Set once a background update check finds and finishes downloading a new version. Applied
    // immediately if the user picks "Restart Now" in UpdateAvailableDialog, or on the next real
    // exit (ExitFromTray) if they picked "Later" -- never during a tray-hide.
    private Velopack.UpdateManager? _pendingUpdateManager;
    private Velopack.UpdateInfo? _pendingUpdateInfo;
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

        AppConfigDataGrid.ItemsSource = _appConfigSettings;
        ElevationInfoBar.IsOpen = !IsRunningAsAdministrator();
        AppVersionTextBlock.Text = $"PublishTool v{GetAppVersion()}";

        _output = new GuiOutputSink(OutputLogBox, StatusTextBlock, _notifyIcon);
        LoadSettingsIntoForm();

        // Fetching the project list can now mean an HTTP call (remote mode) -- defer it to Loaded
        // instead of blocking the constructor.
        Loaded += async (_, _) =>
        {
            await RefreshProjectsAsync();
            await RefreshEnvironmentsAsync();
            await RefreshDependenciesAsync(showDialogIfMissing: true);
            await CheckForUpdatesAsync();
        };
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
        // Apply any update that was downloaded and deferred (user picked "Later" in
        // UpdateAvailableDialog) now, since this is a real exit and not just a tray-hide.
        if (_pendingUpdateManager is not null && _pendingUpdateInfo is not null)
        {
            _pendingUpdateManager.WaitExitThenApplyUpdates(_pendingUpdateInfo.TargetFullRelease);
        }

        _isExiting = true;
        Close();
    }

    // Runs unattended on every launch: silent if there's nothing new, and still silent while
    // downloading (aside from the status bar) once something is found -- the only thing the user
    // actually sees is UpdateAvailableDialog, and only once a new version is fully staged and
    // ready to go.
    // GitHub's unauthenticated API rate limit is 60 requests/hour *per IP* -- fine for one
    // person, but a whole team behind the same office NAT (especially if everyone's machine
    // launches PublishTool around the same time via "Start on Windows startup") can exhaust that
    // shared budget easily, since it's also shared with every other unrelated tool on that
    // network hitting GitHub. Two cheap, no-tradeoff mitigations: don't check more than once per
    // UpdateCheckIntervalHours per machine (regardless of how many times the app is launched --
    // even a *failed* check still counts, so a rate-limit error doesn't just get retried on the
    // next launch minutes later), and spread out exactly when each machine checks so a bunch of
    // simultaneous morning launches don't all land in the same instant.
    private const int UpdateCheckIntervalHours = 6;

    private static string LastUpdateCheckPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PublishTool", "last-update-check.txt");

    private static bool ShouldCheckForUpdates()
    {
        try
        {
            if (!File.Exists(LastUpdateCheckPath))
            {
                return true;
            }

            var lastCheck = DateTimeOffset.Parse(File.ReadAllText(LastUpdateCheckPath), System.Globalization.CultureInfo.InvariantCulture);
            return DateTimeOffset.UtcNow - lastCheck >= TimeSpan.FromHours(UpdateCheckIntervalHours);
        }
        catch
        {
            // Can't tell when we last checked (missing/corrupt file) -- default to checking rather
            // than silently never checking again.
            return true;
        }
    }

    private static void RecordUpdateCheckNow()
    {
        try
        {
            var dir = Path.GetDirectoryName(LastUpdateCheckPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(LastUpdateCheckPath, DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        }
        catch
        {
            // Best-effort -- worst case this machine just checks again next launch instead of
            // waiting out the interval, same as before this existed.
        }
    }

    private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        CheckForUpdatesButton.IsEnabled = false;
        try
        {
            await CheckForUpdatesAsync(interactive: true);
        }
        finally
        {
            CheckForUpdatesButton.IsEnabled = true;
        }
    }

    /// <summary>Read-only GitHub token baked in at build time (see the
    /// <c>GithubUpdateToken</c>/<c>AssemblyMetadata</c> entry in PublishTool.Gui.csproj) -- raises
    /// the update check's rate limit from 60/hour (unauthenticated) to 5000/hour. Null for a local
    /// build with no token supplied, which just means the check stays unauthenticated, same as
    /// before this existed.</summary>
    private static readonly string? GithubUpdateToken = Assembly.GetExecutingAssembly()
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "GithubUpdateToken") is { Value.Length: > 0 } attribute
            ? attribute.Value
            : null;

    /// <param name="interactive">True from the Help tab's "Check for updates" button -- always
    /// actually checks (skips the throttle/jitter below, both meant only to keep the automatic
    /// background check from hammering GitHub) and reports the outcome via a message box even when
    /// there's nothing new, instead of staying silent the way the automatic per-launch check does.</param>
    private async Task CheckForUpdatesAsync(bool interactive = false)
    {
        try
        {
            var mgr = new Velopack.UpdateManager(new Velopack.Sources.GithubSource("https://github.com/marhano/lazyly", GithubUpdateToken, false));
            if (!mgr.IsInstalled)
            {
                // Running from `dotnet run`/a loose build rather than a Velopack-installed copy --
                // there's no installed app for an update to apply to.
                if (interactive)
                {
                    MessageBox.Show(
                        "This copy of PublishTool wasn't installed via the updater (e.g. a dev build) -- there's nothing to check.",
                        "PublishTool", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                return;
            }

            if (!interactive && !ShouldCheckForUpdates())
            {
                return;
            }

            // Recorded before the network call itself (not just on success) so a rate-limited or
            // otherwise failed check still backs off for the full interval instead of being retried
            // on the very next launch.
            RecordUpdateCheckNow();
            if (!interactive)
            {
                await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(0, 180)));
            }

            var newVersion = await mgr.CheckForUpdatesAsync();
            if (newVersion is null)
            {
                if (interactive)
                {
                    MessageBox.Show("You're on the latest version.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                return;
            }

            var previousStatus = StatusTextBlock.Text;
            PublishProgressBar.Visibility = Visibility.Visible;
            StatusTextBlock.Text = "Downloading update...";
            try
            {
                await mgr.DownloadUpdatesAsync(newVersion);
            }
            finally
            {
                PublishProgressBar.Visibility = Visibility.Collapsed;
                StatusTextBlock.Text = previousStatus;
            }

            _pendingUpdateManager = mgr;
            _pendingUpdateInfo = newVersion;

            var versionText = newVersion.TargetFullRelease.Version.ToString();
            var releaseNotes = newVersion.TargetFullRelease.NotesMarkdown;
            var dialog = new UpdateAvailableDialog(versionText, releaseNotes) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                mgr.ApplyUpdatesAndRestart(newVersion.TargetFullRelease);
            }
        }
        catch (Exception ex)
        {
            if (interactive)
            {
                MessageBox.Show($"Couldn't check for updates: {ex.Message}", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                // The automatic background check is best-effort -- a network hiccup or an
                // unreachable/still-private repo shouldn't interrupt anything the user is doing.
                _output.Info($"Update check failed: {ex.Message}");
            }
        }
    }

    private const string StartupTaskName = "PublishTool";

    // PublishTool requires Administrator (see app.manifest), which rules out the classic
    // HKCU...\Run registry approach for "start at login" -- Windows deliberately never elevates
    // anything launched from that key, so an admin-required exe placed there would just silently
    // fail to start with no error shown anywhere. A Scheduled Task with "run with highest
    // privileges" is the standard workaround: for an account that's already an administrator, it
    // launches elevated at logon without a UAC prompt.
    private static bool IsStartupEnabled()
    {
        var (exitCode, _) = RunSchtasks("/Query", "/TN", StartupTaskName);
        return exitCode == 0;
    }

    private static void SetStartupEnabled(bool enabled)
    {
        if (!enabled)
        {
            RunSchtasks("/Delete", "/TN", StartupTaskName, "/F");
            return;
        }

        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (exePath is null)
        {
            return;
        }

        RunSchtasks("/Create", "/TN", StartupTaskName, "/TR", $"\"{exePath}\"", "/SC", "ONLOGON", "/RL", "HIGHEST", "/F");
    }

    private static (int ExitCode, string Output) RunSchtasks(params string[] arguments)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
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
        UseRemoteModeToggle.IsEnabled = remoteHostingConfigured;
        if (!remoteHostingConfigured)
        {
            UseRemoteModeToggle.IsChecked = false;
        }
        else
        {
            UseRemoteModeToggle.IsChecked = settings.UseRemoteMode;
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

    private async void ExportProjectsButton_Click(object sender, RoutedEventArgs e)
    {
        var registry = ProjectRegistryFactory.Create();
        var projects = await registry.GetProjectsAsync();
        if (projects.Count == 0)
        {
            MessageBox.Show("There are no registered projects to export yet.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var pickDialog = new ExportProjectsDialog(projects.Select(p => p.Name)) { Owner = this };
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
            var selected = new List<ProjectConfig>();
            foreach (var name in pickDialog.SelectedProjectNames)
            {
                if (await registry.GetAsync(name) is { } p)
                {
                    selected.Add(p);
                }
            }

            ProjectConfigPortability.Export(selected, saveDialog.FileName);
            _output.Info($"Exported {pickDialog.SelectedProjectNames.Count} project(s) to {saveDialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't export: {ex.Message}", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ImportProjectsButton_Click(object sender, RoutedEventArgs e)
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

        var registry = ProjectRegistryFactory.Create();
        var preview = await ProjectConfigPortability.PreviewAsync(file, registry);

        var previewDialog = new ImportProjectsDialog(file, preview) { Owner = this };
        if (previewDialog.ShowDialog() != true)
        {
            return;
        }

        await ProjectConfigPortability.ImportAsync(file, registry, previewDialog.SelectedProjectNames);
        _output.Info($"Imported {previewDialog.SelectedProjectNames.Count} project(s) from {openDialog.FileName}");
        await RefreshProjectsAsync();
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

    private async void UseRemoteModeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        settings.UseRemoteMode = UseRemoteModeToggle.IsChecked == true;
        settings.Save(AppSettings.DefaultPath);

        // Projects tab, IIS tab, Firewall tab, and the environment list now read from a
        // different source -- refresh all of them immediately so the switch is visible right
        // away instead of on the next unrelated action. The Publish tab's "Remote" deploy option
        // also depends on this.
        await RefreshProjectsAsync();
        await RefreshIisStatusAsync();
        await RefreshFirewallStatusAsync();
        await RefreshEnvironmentsAsync();
        await LoadDeployTargetOptionsForSelectedProjectAsync();
    }

    private async Task RefreshEnvironmentsAsync()
    {
        var settings = await EnvironmentRegistryFactory.Create().GetAsync();
        EnvironmentsListBox.ItemsSource = settings.Names
            .Select(name => string.Equals(name, settings.DefaultName, StringComparison.OrdinalIgnoreCase) ? $"{name} (default)" : name)
            .ToList();
    }

    /// <summary>Strips the " (default)" suffix <see cref="RefreshEnvironmentsAsync"/> displays with,
    /// so callers acting on the selected list item get the real environment name back.</summary>
    private static string StripDefaultSuffix(string displayName) =>
        displayName.EndsWith(" (default)", StringComparison.Ordinal) ? displayName[..^" (default)".Length] : displayName;

    private async void AddEnvironmentButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NewEnvironmentNameTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Enter an environment name first.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var registry = EnvironmentRegistryFactory.Create();
        var settings = await registry.GetAsync();
        if (settings.Names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show($"'{name}' already exists.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        settings.Names.Add(name);
        settings.DefaultName ??= name;
        await registry.SaveAsync(settings);

        NewEnvironmentNameTextBox.Clear();
        await RefreshEnvironmentsAsync();
    }

    private async void RemoveEnvironmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (EnvironmentsListBox.SelectedItem is not string selected)
        {
            MessageBox.Show("Select an environment in the list first.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var name = StripDefaultSuffix(selected);
        var registry = EnvironmentRegistryFactory.Create();
        var settings = await registry.GetAsync();
        settings.Names.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(settings.DefaultName, name, StringComparison.OrdinalIgnoreCase))
        {
            settings.DefaultName = settings.Names.FirstOrDefault();
        }

        await registry.SaveAsync(settings);
        await RefreshEnvironmentsAsync();
    }

    private async void MakeDefaultEnvironmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (EnvironmentsListBox.SelectedItem is not string selected)
        {
            MessageBox.Show("Select an environment in the list first.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var registry = EnvironmentRegistryFactory.Create();
        var settings = await registry.GetAsync();
        settings.DefaultName = StripDefaultSuffix(selected);
        await registry.SaveAsync(settings);
        await RefreshEnvironmentsAsync();
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
            ShowOutputPanel();
            return;
        }

        OutputPanel.Visibility = Visibility.Collapsed;
        OutputSplitter.Visibility = Visibility.Collapsed;
        ToggleOutputButton.Content = "Show output";
    }

    /// <summary>Expands the Output panel if it's currently collapsed -- a no-op otherwise. Used by
    /// the toggle button itself and, separately, to make sure a Projects-tab deploy's progress is
    /// actually visible without the user having to remember they hid it earlier.</summary>
    private void ShowOutputPanel()
    {
        if (OutputPanel.Visibility == Visibility.Visible)
        {
            return;
        }

        OutputColumnDefinition.MinWidth = 260;
        OutputColumnDefinition.Width = _savedOutputColumnWidth;
        OutputPanel.Visibility = Visibility.Visible;
        OutputSplitter.Visibility = Visibility.Visible;
        ToggleOutputButton.Content = "Hide output";
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
        else if (MainTabControl.SelectedItem is TabItem { Header: "Firewall" })
        {
            await RefreshFirewallStatusAsync();
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

    /// <summary>True when the IIS/Projects tabs should talk to the dev server instead of this
    /// machine's own IIS/project registry -- both the mode toggle and a configured URL are needed.</summary>
    private static bool IsRemoteModeActive(out AppSettings settings)
    {
        settings = AppSettings.Load(AppSettings.DefaultPath);
        return settings.UseRemoteMode && !string.IsNullOrWhiteSpace(settings.RemoteHostingUrl);
    }

#pragma warning disable CA1416 // DPAPI (SecretProtector) is Windows-only; this whole tool only ever runs on Windows despite some projects' plain net8.0 TFM.
    private static string? DecryptRemoteHostingApiKey(AppSettings settings) =>
        settings.RemoteHostingProtectedApiKey is null
            ? null
            : SecretProtector.TryUnprotect(settings.RemoteHostingProtectedApiKey, SecretProtector.RemoteHostingPurpose);
#pragma warning restore CA1416

    private async Task RefreshIisStatusAsync()
    {
        try
        {
            if (IsRemoteModeActive(out var settings))
            {
                var client = new RemoteHostingClient();
                var apiKey = DecryptRemoteHostingApiKey(settings);
                IisSitesDataGrid.ItemsSource = await client.ListRemoteSitesAsync(settings.RemoteHostingUrl!, apiKey);
                IisAppPoolsDataGrid.ItemsSource = await client.ListRemoteAppPoolsAsync(settings.RemoteHostingUrl!, apiKey);
            }
            else
            {
                var manager = new IisSiteManager(_output);
                IisSitesDataGrid.ItemsSource = await manager.ListSitesAsync();
                IisAppPoolsDataGrid.ItemsSource = await manager.ListAppPoolsAsync();
            }

            ApplyIisSiteStateFilter();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void IisSiteStateFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyIisSiteStateFilter();

    /// <summary>Client-side filter over whatever's currently loaded -- same pattern as the Event
    /// Logs tab's Level/Method/Type filters, just one combo instead of three.</summary>
    private void ApplyIisSiteStateFilter()
    {
        // IisSiteStateFilterComboBox's SelectedIndex="0" (set in XAML) fires SelectionChanged
        // during InitializeComponent() itself, before IisSitesDataGrid -- declared later in the
        // same XAML tree -- has been assigned yet, so this can run with either field still null.
        if (IisSitesDataGrid is null || IisSitesDataGrid.ItemsSource is null)
        {
            return;
        }

        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(IisSitesDataGrid.ItemsSource);
        var selection = (IisSiteStateFilterComboBox.SelectedItem as ComboBoxItem)?.Content as string;
        view.Filter = selection switch
        {
            "Started" => item => item is IisSiteStatus site && string.Equals(site.State, "Started", StringComparison.OrdinalIgnoreCase),
            "Stopped" => item => item is IisSiteStatus site && string.Equals(site.State, "Stopped", StringComparison.OrdinalIgnoreCase),
            _ => null,
        };
    }

    private async void StartSiteButton_Click(object sender, RoutedEventArgs e)
    {
        if (IisSitesDataGrid.SelectedItem is not IisSiteStatus site)
        {
            MessageBox.Show("Select a site first.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (IsRemoteModeActive(out var settings))
        {
            await new RemoteHostingClient().StartRemoteSiteAsync(settings.RemoteHostingUrl!, DecryptRemoteHostingApiKey(settings), site.Name);
        }
        else
        {
            await RunAsync(new[] { "iis-start-site", "--name", site.Name });
        }

        await RefreshIisStatusAsync();
    }

    private async void StopSiteButton_Click(object sender, RoutedEventArgs e)
    {
        if (IisSitesDataGrid.SelectedItem is not IisSiteStatus site)
        {
            MessageBox.Show("Select a site first.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (IsRemoteModeActive(out var settings))
        {
            await new RemoteHostingClient().StopRemoteSiteAsync(settings.RemoteHostingUrl!, DecryptRemoteHostingApiKey(settings), site.Name);
        }
        else
        {
            await RunAsync(new[] { "iis-stop-site", "--name", site.Name });
        }

        await RefreshIisStatusAsync();
    }

    private void BrowseSiteButton_Click(object sender, RoutedEventArgs e)
    {
        if (IisSitesDataGrid.SelectedItem is not IisSiteStatus site)
        {
            MessageBox.Show("Select a site first.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // A wildcard/no-hostname binding is ambiguous -- "localhost" is right for this machine's
        // own IIS, but wrong when the site being browsed lives on the *remote* dev server instead.
        // Use the dev server's own hostname (its Remote Build Hosting URL, minus the port -- the
        // site has its own port from its own binding) in that case.
        var fallbackHost = IsRemoteModeActive(out var settings) && Uri.TryCreate(settings.RemoteHostingUrl, UriKind.Absolute, out var hostingUri)
            ? hostingUri.Host
            : "localhost";

        var url = BuildBrowseUrl(site.Bindings, fallbackHost);
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
    // and falls back to fallbackHost when the binding's IP is "*" (all unassigned) with no hostname.
    private static string? BuildBrowseUrl(string bindingsRaw, string fallbackHost)
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
            : fallbackHost;

        var isDefaultPort = (protocol == "http" && port == "80") || (protocol == "https" && port == "443");
        return isDefaultPort ? $"{protocol}://{hostname}/" : $"{protocol}://{hostname}:{port}/";
    }

    private async void SiteHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (IisSitesDataGrid.SelectedItem is not IisSiteStatus site)
        {
            MessageBox.Show("Select a site first.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            IReadOnlyList<SiteDeploymentRecord> history;
            if (IsRemoteModeActive(out var settings))
            {
                history = await new RemoteHostingClient().GetRemoteSiteDeploymentHistoryAsync(
                    settings.RemoteHostingUrl!, DecryptRemoteHostingApiKey(settings), site.Name);
            }
            else
            {
                history = await new IisSiteManager(_output).GetDeploymentHistoryAsync(site.Name);
            }

            new DeploymentHistoryDialog(site.Name, history) { Owner = this }.ShowDialog();
        }
        catch (RemoteFeatureNotAvailableException ex)
        {
            MessageBox.Show(ex.Message, "PublishTool", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't load deployment history: {ex.Message}", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void StartAppPoolButton_Click(object sender, RoutedEventArgs e)
    {
        if (IisAppPoolsDataGrid.SelectedItem is not IisAppPoolStatus pool)
        {
            MessageBox.Show("Select an application pool first.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (IsRemoteModeActive(out var settings))
        {
            await new RemoteHostingClient().StartRemoteAppPoolAsync(settings.RemoteHostingUrl!, DecryptRemoteHostingApiKey(settings), pool.Name);
        }
        else
        {
            await RunAsync(new[] { "iis-start-apppool", "--name", pool.Name });
        }

        await RefreshIisStatusAsync();
    }

    private async void StopAppPoolButton_Click(object sender, RoutedEventArgs e)
    {
        if (IisAppPoolsDataGrid.SelectedItem is not IisAppPoolStatus pool)
        {
            MessageBox.Show("Select an application pool first.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (IsRemoteModeActive(out var settings))
        {
            await new RemoteHostingClient().StopRemoteAppPoolAsync(settings.RemoteHostingUrl!, DecryptRemoteHostingApiKey(settings), pool.Name);
        }
        else
        {
            await RunAsync(new[] { "iis-stop-apppool", "--name", pool.Name });
        }

        await RefreshIisStatusAsync();
    }

    private async void RecycleAppPoolButton_Click(object sender, RoutedEventArgs e)
    {
        if (IisAppPoolsDataGrid.SelectedItem is not IisAppPoolStatus pool)
        {
            MessageBox.Show("Select an application pool first.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (IsRemoteModeActive(out var settings))
        {
            await new RemoteHostingClient().RecycleRemoteAppPoolAsync(settings.RemoteHostingUrl!, DecryptRemoteHostingApiKey(settings), pool.Name);
        }
        else
        {
            await RunAsync(new[] { "iis-recycle-apppool", "--name", pool.Name });
        }

        await RefreshIisStatusAsync();
    }

    private async void RefreshFirewallButton_Click(object sender, RoutedEventArgs e) => await RefreshFirewallStatusAsync();

    private async void ShowAllFirewallRulesToggle_Toggled(object sender, RoutedEventArgs e) => await RefreshFirewallStatusAsync();

    private async Task RefreshFirewallStatusAsync()
    {
        try
        {
            var includeAllRules = ShowAllFirewallRulesToggle.IsChecked == true;
            if (IsRemoteModeActive(out var settings))
            {
                FirewallRulesDataGrid.ItemsSource = await new RemoteHostingClient().ListRemoteFirewallRulesAsync(
                    settings.RemoteHostingUrl!, DecryptRemoteHostingApiKey(settings), includeAllRules);
            }
            else
            {
                FirewallRulesDataGrid.ItemsSource = await new FirewallManager(_output).ListRulesAsync(includeAllRules);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void AddFirewallRuleButton_Click(object sender, RoutedEventArgs e)
    {
        var label = FirewallRuleLabelTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(label))
        {
            MessageBox.Show("Enter a label for the rule first.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var ports = FirewallRulePortTextBox.Text?.Trim() ?? string.Empty;
        try
        {
            FirewallManager.ValidatePortSpec(ports);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var protocol = (FirewallRuleProtocolComboBox.SelectedItem as ComboBoxItem)?.Content as string ?? "TCP";
        var performedBy = Environment.UserName;

        try
        {
            if (IsRemoteModeActive(out var settings))
            {
                await new RemoteHostingClient().AddRemoteFirewallRuleAsync(
                    settings.RemoteHostingUrl!, DecryptRemoteHostingApiKey(settings), label, ports, protocol, performedBy);
            }
            else
            {
                await new FirewallManager(_output).AddInboundRuleAsync(label, ports, protocol, performedBy);
            }

            FirewallRuleLabelTextBox.Clear();
            FirewallRulePortTextBox.Clear();
            await RefreshFirewallStatusAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't add the firewall rule: {ex.Message}", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>True (and shows the guard message) for a row that isn't one of PublishTool's own
    /// rules -- only reachable when "Show all rules" is on, since the grid is filtered otherwise.</summary>
    private static bool WarnIfNotOwnRule(FirewallRuleStatus rule)
    {
        if (rule.Name.StartsWith(FirewallManager.RuleNamePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        MessageBox.Show(
            "Only rules PublishTool created can be edited/removed here.",
            "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
        return true;
    }

    private async void EditFirewallRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (FirewallRulesDataGrid.SelectedItem is not FirewallRuleStatus rule)
        {
            MessageBox.Show("Select a rule first.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (WarnIfNotOwnRule(rule))
        {
            return;
        }

        var currentLabel = rule.Name[FirewallManager.RuleNamePrefix.Length..];
        var dialog = new EditFirewallRuleDialog(currentLabel, rule.Port, rule.Protocol) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var performedBy = Environment.UserName;
        try
        {
            if (IsRemoteModeActive(out var settings))
            {
                await new RemoteHostingClient().EditRemoteFirewallRuleAsync(
                    settings.RemoteHostingUrl!, DecryptRemoteHostingApiKey(settings),
                    rule.Name, dialog.Label, dialog.Ports, dialog.Protocol, performedBy);
            }
            else
            {
                await new FirewallManager(_output).EditRuleAsync(rule.Name, dialog.Label, dialog.Ports, dialog.Protocol, performedBy);
            }

            await RefreshFirewallStatusAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't update the firewall rule: {ex.Message}", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RemoveFirewallRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (FirewallRulesDataGrid.SelectedItem is not FirewallRuleStatus rule)
        {
            MessageBox.Show("Select a rule first.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (WarnIfNotOwnRule(rule))
        {
            return;
        }

        var confirm = MessageBox.Show(
            $"Remove the firewall rule '{rule.Name}'? Anything relying on that port being open from outside will stop being reachable.",
            "PublishTool", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        var performedBy = Environment.UserName;
        try
        {
            if (IsRemoteModeActive(out var settings))
            {
                await new RemoteHostingClient().DeleteRemoteFirewallRuleAsync(
                    settings.RemoteHostingUrl!, DecryptRemoteHostingApiKey(settings), rule.Name, performedBy);
            }
            else
            {
                await new FirewallManager(_output).DeleteRuleAsync(rule.Name, performedBy);
            }

            await RefreshFirewallStatusAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't remove the firewall rule: {ex.Message}", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void FirewallHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            IReadOnlyList<FirewallAuditEntry> history;
            if (IsRemoteModeActive(out var settings))
            {
                history = await new RemoteHostingClient().GetRemoteFirewallAuditAsync(settings.RemoteHostingUrl!, DecryptRemoteHostingApiKey(settings));
            }
            else
            {
                history = await new FirewallManager(_output).GetAuditHistoryAsync();
            }

            new FirewallAuditDialog(history) { Owner = this }.ShowDialog();
        }
        catch (RemoteFeatureNotAvailableException ex)
        {
            MessageBox.Show(ex.Message, "PublishTool", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't load the firewall audit trail: {ex.Message}", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RefreshProjectsButton_Click(object sender, RoutedEventArgs e) => await RefreshProjectsAsync(forceReloadSelectedProject: true);

    private async void RefreshRegisteredProjectsButton_Click(object sender, RoutedEventArgs e) => await RefreshProjectsAsync(forceReloadSelectedProject: true);

    /// <param name="forceReloadSelectedProject">Every call site reloads the project name/list
    /// controls themselves, but by default leaves the Publish tab's App Config grid and deploy
    /// target options alone if the same project is still selected afterward -- otherwise the
    /// silent re-selection this causes after every command (see below) would wipe out App Config
    /// edits or release notes someone's still typing. Explicit user-clicked refresh buttons pass
    /// true instead, since the whole point of clicking one is to pick up a teammate's changes to
    /// the currently-selected project too, not just notice a project that's new or gone.</param>
    private async Task RefreshProjectsAsync(bool forceReloadSelectedProject = false)
    {
        var registry = ProjectRegistryFactory.Create();
        var projects = await registry.GetProjectsAsync();

        var selectedInCombo = ProjectComboBox.SelectedItem as string;
        ProjectComboBox.ItemsSource = projects.Select(p => p.Name).ToList();
        if (selectedInCombo is not null)
        {
            ProjectComboBox.SelectedItem = selectedInCombo;
        }

        var selectedInList = (RegisteredProjectsListBox.SelectedItem as ProjectConfig)?.Name;
        RegisteredProjectsListBox.ItemsSource = projects;
        if (selectedInList is not null)
        {
            RegisteredProjectsListBox.SelectedItem = projects.FirstOrDefault(p => string.Equals(p.Name, selectedInList, StringComparison.OrdinalIgnoreCase));
        }

        // All projects, not just ones with Event Logs already enabled -- filtering the list down
        // used to leave this combo silently empty with no explanation for anyone who hadn't yet
        // turned the feature on for a project. LoadEventLogsForSelectedProjectAsync now says so
        // explicitly instead.
        var selectedInEventLogCombo = EventLogProjectComboBox.SelectedItem as string;
        EventLogProjectComboBox.ItemsSource = projects.Select(p => p.Name).ToList();
        if (selectedInEventLogCombo is not null)
        {
            EventLogProjectComboBox.SelectedItem = selectedInEventLogCombo;
        }

        if (forceReloadSelectedProject && selectedInCombo is not null)
        {
            await LoadAppConfigForSelectedProjectAsync();
            await LoadDeployTargetOptionsForSelectedProjectAsync();
        }
    }

    private async void ProjectComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var project = ProjectComboBox.SelectedItem as string;
        var changed = !string.Equals(project, _lastSelectedProjectForForm, StringComparison.Ordinal);
        _lastSelectedProjectForForm = project;

        await LoadVersionsForSelectedProjectAsync();

        // Only reset the form when the project actually changed -- ProjectComboBox gets
        // reselected (to the same value) after every command via RefreshProjectsAsync(), and that
        // shouldn't wipe out release notes the user is still editing.
        if (changed)
        {
            VersionComboBox.Text = string.Empty;
            VersionOverwriteHintTextBlock.Visibility = Visibility.Collapsed;
            FeaturesEditor.Clear();
            FixesEditor.Clear();
            OtherUpdatesEditor.Clear();
            BacklogItemsEditor.Clear();
            await LoadAppConfigForSelectedProjectAsync();
            await LoadDeployTargetOptionsForSelectedProjectAsync();
        }

        await LoadGitBranchesForSelectedProjectAsync();
    }

    /// <summary>Populates the Publish tab's "Deploy target" select with "Local"/"Remote" -- only
    /// the sides this project actually has IIS deployment enabled for (and, for Remote, only while
    /// remote mode is on). Hides the whole deploy-selection panel entirely when neither side is
    /// available, so publishing then just archives/uploads with no deploy step.</summary>
    private async Task LoadDeployTargetOptionsForSelectedProjectAsync()
    {
        var projectName = ProjectComboBox.SelectedItem as string;
        var project = string.IsNullOrWhiteSpace(projectName) ? null : await ProjectRegistryFactory.Create().GetAsync(projectName);

        var targets = new List<string>();
        if (project?.LocalIisEnabled == true)
        {
            targets.Add("Local");
        }

        if (project?.RemoteIisEnabled == true && AppSettings.Load(AppSettings.DefaultPath).UseRemoteMode)
        {
            targets.Add("Remote");
        }

        if (targets.Count == 0)
        {
            DeploySelectionPanel.Visibility = Visibility.Collapsed;
            DeployTargetComboBox.ItemsSource = null;
            DeployEnvironmentComboBox.ItemsSource = null;
            return;
        }

        DeploySelectionPanel.Visibility = Visibility.Visible;
        DeployTargetComboBox.ItemsSource = targets;
        DeployTargetComboBox.SelectedIndex = 0;
        await PopulateDeployEnvironmentComboBoxAsync();
    }

    private async void DeployTargetComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        await PopulateDeployEnvironmentComboBoxAsync();

    /// <summary>Synchronous counterpart to <see cref="LoadDeployTargetOptionsForSelectedProjectAsync"/>
    /// for the Projects tab, which already has the full <see cref="ProjectConfig"/> in hand (no
    /// need to reload it). Applies the identical two gating conditions -- Local always available
    /// when enabled, Remote only when enabled *and* remote mode is on -- so a project's "Deploy
    /// this version" button (gated on this being non-empty) never offers a target the Publish tab
    /// itself wouldn't.</summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> GetAvailableDeployTargets(ProjectConfig project)
    {
        var targets = new Dictionary<string, IReadOnlyList<string>>();
        if (project.LocalIisEnabled)
        {
            targets["Local"] = project.LocalEnvironments.Select(env => env.Name).ToList();
        }

        if (project.RemoteIisEnabled && AppSettings.Load(AppSettings.DefaultPath).UseRemoteMode)
        {
            targets["Remote"] = project.RemoteEnvironments.Select(env => env.Name).ToList();
        }

        return targets;
    }

    /// <summary>Populates the "Deploy to" select with the environment names configured on whichever
    /// side "Deploy target" currently has selected, defaulting to the Settings-tab default
    /// environment when it's among them.</summary>
    private async Task PopulateDeployEnvironmentComboBoxAsync()
    {
        var target = DeployTargetComboBox.SelectedItem as string;
        if (target is null)
        {
            return;
        }

        var projectName = ProjectComboBox.SelectedItem as string;
        var project = string.IsNullOrWhiteSpace(projectName) ? null : await ProjectRegistryFactory.Create().GetAsync(projectName);

        var names = target == "Local"
            ? project?.LocalEnvironments.Select(env => env.Name).ToList() ?? new List<string>()
            : project?.RemoteEnvironments.Select(env => env.Name).ToList() ?? new List<string>();

        DeployEnvironmentComboBox.ItemsSource = names;

        string? defaultName = null;
        try
        {
            defaultName = (await EnvironmentRegistryFactory.Create().GetAsync()).DefaultName;
        }
        catch (Exception ex)
        {
            _output.Warn($"Couldn't load the default deployment environment: {ex.Message}");
        }

        DeployEnvironmentComboBox.SelectedItem = defaultName is not null && names.Contains(defaultName, StringComparer.OrdinalIgnoreCase)
            ? names.First(n => string.Equals(n, defaultName, StringComparison.OrdinalIgnoreCase))
            : names.FirstOrDefault();
    }

    /// <summary>Shows/hides the App Config accordion for the selected project and, if it uses
    /// app config, seeds the grid from the live config file on disk (the starting point before
    /// the user picks a specific already-published version, which would show that version's
    /// saved settings instead -- see VersionComboBox_SelectionChanged).</summary>
    private async Task LoadAppConfigForSelectedProjectAsync()
    {
        _appConfigSettings.Clear();

        var projectName = ProjectComboBox.SelectedItem as string;
        var project = string.IsNullOrWhiteSpace(projectName) ? null : await ProjectRegistryFactory.Create().GetAsync(projectName);

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

    private async Task LoadVersionsForSelectedProjectAsync()
    {
        _currentProjectVersions.Clear();

        var project = ProjectComboBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(project))
        {
            VersionComboBox.ItemsSource = null;
            return;
        }

        List<string> versions;
        try
        {
            if (IsRemoteModeActive(out var settings))
            {
                var builds = await new RemoteHostingClient().ListBuildsAsync(
                    settings.RemoteHostingUrl!, DecryptRemoteHostingApiKey(settings), project);
                versions = builds.Select(b => b.Version).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            else
            {
                var localSettings = AppSettings.Load(AppSettings.DefaultPath);
                var buildRepository = new BuildRepository();
                versions = buildRepository.ListBuilds(localSettings.BuildsRoot, project)
                    .Select(b => b.Version)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't load existing versions: {ex.Message}", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            versions = new List<string>();
        }

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

        var registry = ProjectRegistryFactory.Create();
        var project = await registry.GetAsync(projectName);
        if (project is null || string.IsNullOrWhiteSpace(project.CsprojPath))
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

        var projectConfig = await ProjectRegistryFactory.Create().GetAsync(project);
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
        await LoadAppConfigForSelectedProjectAsync();
        await LoadGitBranchesForSelectedProjectAsync();
    }

    private async Task CheckoutBranchAsync(ProjectConfig project, string branch)
    {
        if (string.IsNullOrWhiteSpace(project.CsprojPath))
        {
            MessageBox.Show(
                $"'{project.Name}' has no .csproj path configured -- set one in the project's Edit dialog first.",
                "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

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
    /// callers can bail out of whatever checkout attempt was waiting on it. Only ever called from
    /// <see cref="CheckoutBranchAsync"/>, which has already confirmed <paramref name="project"/>
    /// has a .csproj path configured before it does any of this.</summary>
    private async Task<bool> ApplyGitResolutionAsync(
        GitService git, ProjectConfig project, string branch, GitConflictResolution resolution, IReadOnlyList<string> files, string commitMessage)
    {
        try
        {
            switch (resolution)
            {
                case GitConflictResolution.Discard:
                    await git.DiscardChangesAsync(project.CsprojPath!, files);
                    break;
                case GitConflictResolution.Stash:
                    await git.StashChangesAsync(project.CsprojPath!, files, $"PublishTool: before checkout to {branch}");
                    break;
                case GitConflictResolution.Commit:
                    await git.CommitChangesAsync(project.CsprojPath!, files, commitMessage);
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

    private async void VersionComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
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
        await LoadAppConfigForSelectedProjectAsync();

        BuildManifest? manifest = null;
        string? releaseNotesText = null;

        try
        {
            if (IsRemoteModeActive(out var settings))
            {
                var client = new RemoteHostingClient();
                var apiKey = DecryptRemoteHostingApiKey(settings);
                var builds = await client.ListBuildsAsync(settings.RemoteHostingUrl!, apiKey, project);
                var match = builds.FirstOrDefault(b => string.Equals(b.Version, version, StringComparison.OrdinalIgnoreCase));

                if (match is not null)
                {
                    var tempManifestPath = Path.Combine(Path.GetTempPath(), $"publishtool-{Guid.NewGuid():N}.manifest.json");
                    try
                    {
                        await client.DownloadAsync(settings.RemoteHostingUrl!, apiKey, match.ManifestPath, tempManifestPath);
                        manifest = JsonSerializer.Deserialize<BuildManifest>(await File.ReadAllTextAsync(tempManifestPath));
                    }
                    finally
                    {
                        if (File.Exists(tempManifestPath)) File.Delete(tempManifestPath);
                    }

                    if (match.ReleaseNotesPath is not null)
                    {
                        var tempNotesPath = Path.Combine(Path.GetTempPath(), $"publishtool-{Guid.NewGuid():N}.releasenotes.txt");
                        try
                        {
                            await client.DownloadAsync(settings.RemoteHostingUrl!, apiKey, match.ReleaseNotesPath, tempNotesPath);
                            releaseNotesText = await File.ReadAllTextAsync(tempNotesPath);
                        }
                        finally
                        {
                            if (File.Exists(tempNotesPath)) File.Delete(tempNotesPath);
                        }
                    }
                }
            }
            else
            {
                var localSettings = AppSettings.Load(AppSettings.DefaultPath);
                var existing = new BuildRepository().FindBuild(localSettings.BuildsRoot, project, version);
                manifest = existing?.Manifest;

                if (manifest?.ReleaseNotesPath is { } notesPath && File.Exists(notesPath))
                {
                    releaseNotesText = await File.ReadAllTextAsync(notesPath);
                }
            }
        }
        catch (Exception ex)
        {
            _output.Warn($"Couldn't load saved details for version {version}: {ex.Message}");
        }

        ApplySavedAppConfig(manifest);

        if (releaseNotesText is null)
        {
            return;
        }

        var entry = ReleaseNotesFormatter.Parse(releaseNotesText);
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
    private void ApplySavedAppConfig(BuildManifest? manifest)
    {
        if (manifest?.AppConfigSettings is not { Count: > 0 } saved)
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

        if (DeploySelectionPanel.Visibility == Visibility.Visible &&
            DeployTargetComboBox.SelectedItem is string deployTarget &&
            DeployEnvironmentComboBox.SelectedItem is string deployEnvironment)
        {
            args.Add("--deploy-target");
            args.Add(deployTarget.ToLowerInvariant());
            args.Add("--environment");
            args.Add(deployEnvironment);
        }

        foreach (var item in FeaturesEditor.Items) { args.Add("--feature"); args.Add(item); }
        foreach (var item in FixesEditor.Items) { args.Add("--fix"); args.Add(item); }
        foreach (var item in OtherUpdatesEditor.Items) { args.Add("--other-update"); args.Add(item); }
        foreach (var item in BacklogItemsEditor.Items) { args.Add("--backlog-item"); args.Add(item); }

        if (MarkAsLatestToggle.IsChecked == true)
        {
            args.Add("--mark-latest");
        }

        if (AppConfigExpander.Visibility == Visibility.Visible)
        {
            // A cell/row still mid-edit (user typed a new value and clicked Publish directly,
            // without tabbing or clicking elsewhere first) hasn't been pushed into the bound
            // AppConfigSettingRow yet at this point -- CommitEdit needs calling twice (cell, then
            // row) to flush both levels before _appConfigSettings is read below, or the edit is
            // silently lost and the build publishes with the previous value.
            AppConfigDataGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            AppConfigDataGrid.CommitEdit(DataGridEditingUnit.Row, true);

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
        await LoadVersionsForSelectedProjectAsync();
    }

    private async void AddProjectButton_Click(object sender, RoutedEventArgs e)
    {
        var remoteMode = IsRemoteModeActive(out _);
        var dialog = new ProjectEditDialog(null, remoteMode) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            await RefreshProjectsAsync();
        }
    }

    private async void EditProjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (RegisteredProjectsListBox.SelectedItem is not ProjectConfig project)
        {
            MessageBox.Show("Select a project in the list first.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var remoteMode = IsRemoteModeActive(out _);
        var dialog = new ProjectEditDialog(project, remoteMode) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            await RefreshProjectsAsync();
        }
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
        await RefreshProjectsAsync();
    }

    private async void RegisteredProjectsListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        await LoadBuildHistoryAsync();

    private async void RefreshBuildsButton_Click(object sender, RoutedEventArgs e) => await LoadBuildHistoryAsync();

    private async Task LoadBuildHistoryAsync()
    {
        if (RegisteredProjectsListBox.SelectedItem is not ProjectConfig project)
        {
            BuildHistoryDataGrid.ItemsSource = null;
            BuildHistoryTitleTextBlock.Text = "Select a project to see its build history";
            return;
        }

        BuildHistoryTitleTextBlock.Text = $"Build history: {project.Name}";
        RefreshBuildsButton.IsEnabled = false;

        try
        {
            List<BuildHistoryRow> rows;
            var canDeploy = GetAvailableDeployTargets(project).Count > 0;

            if (IsRemoteModeActive(out var settings))
            {
                var builds = await new RemoteHostingClient().ListBuildsAsync(
                    settings.RemoteHostingUrl!, DecryptRemoteHostingApiKey(settings), project.Name);
                rows = builds.Select(b => new BuildHistoryRow
                {
                    Version = b.Version,
                    PublishedAtUtc = b.PublishedAtUtc,
                    PublishedBy = b.PublishedBy,
                    IsLatest = b.IsLatest,
                    ListInHosting = b.ListInHosting,
                    RemoteManifestPath = b.ManifestPath,
                    RemoteZipPath = b.ZipPath,
                    CanDeploy = canDeploy,
                }).ToList();
            }
            else
            {
                var appSettings = AppSettings.Load(AppSettings.DefaultPath);
                var builds = await Task.Run(() => new BuildRepository().ListBuildsWithPaths(appSettings.BuildsRoot, project.Name));
                rows = builds.Select(b => new BuildHistoryRow
                {
                    Version = b.Manifest.Version,
                    PublishedAtUtc = b.Manifest.PublishedAtUtc,
                    PublishedBy = b.Manifest.PublishedBy,
                    IsLatest = b.Manifest.IsLatest,
                    ListInHosting = b.Manifest.ListInHosting,
                    ManifestPath = b.ManifestPath,
                    ZipPath = b.Manifest.ZipPath,
                    CanDeploy = canDeploy,
                }).ToList();
            }

            BuildHistoryDataGrid.ItemsSource = rows;
        }
        catch (Exception ex)
        {
            BuildHistoryDataGrid.ItemsSource = null;
            MessageBox.Show($"Couldn't load build history: {ex.Message}", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            RefreshBuildsButton.IsEnabled = true;
        }
    }

    private async void DeployBuildButton_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not BuildHistoryRow row ||
            RegisteredProjectsListBox.SelectedItem is not ProjectConfig project)
        {
            return;
        }

        var targets = GetAvailableDeployTargets(project);
        if (targets.Count == 0)
        {
            MessageBox.Show(
                $"Neither Local nor Remote IIS is enabled for '{project.Name}' -- turn one on in the project's Edit dialog first.",
                "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string target;
        string environmentName;
        if (targets.Count == 1 && targets.Values.First().Count == 1)
        {
            target = targets.Keys.First();
            environmentName = targets.Values.First()[0];
        }
        else
        {
            var picker = new EnvironmentPickerDialog(project.Name, row.Version, targets) { Owner = this };
            if (picker.ShowDialog() != true || picker.SelectedTarget is not { } chosenTarget || picker.SelectedEnvironment is not { } chosenEnvironment)
            {
                return;
            }

            target = chosenTarget;
            environmentName = chosenEnvironment;
        }

        var environments = target == "Local" ? project.LocalEnvironments : project.RemoteEnvironments;
        var environment = environments.FirstOrDefault(env => string.Equals(env.Name, environmentName, StringComparison.OrdinalIgnoreCase));
        if (environment is null)
        {
            MessageBox.Show(
                $"'{project.Name}' has no {(target == "Local" ? "local" : "dev-server")} environment named '{environmentName}' -- " +
                "add it in the project's Edit dialog first.",
                "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            $"Deploy {project.Name} v{row.Version} to {target} '{environment.Name}' now? This overwrites whatever is currently live there.",
            "PublishTool", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        ShowOutputPanel();
        _output.Stage($"Deploying {project.Name} v{row.Version} to {target} ({environment.Name})...");
        SetBusy(true);
        BuildHistoryDataGrid.IsEnabled = false;
        try
        {
            if (target == "Remote")
            {
                if (!IsRemoteModeActive(out var settings) || row.RemoteManifestPath is null)
                {
                    MessageBox.Show(
                        "Remote deploy needs remote mode configured in Settings and an already-uploaded build.",
                        "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                await new RemoteHostingClient().DeployAsync(
                    settings.RemoteHostingUrl!, DecryptRemoteHostingApiKey(settings), row.RemoteManifestPath, environment.Name, Environment.UserName);
                _output.Info($"Deployed {project.Name} v{row.Version} to the dev server ({environment.Name}).");
            }
            else
            {
                var hostPath = environment.ResolveHostPath(project.Name);
                if (hostPath is null)
                {
                    MessageBox.Show(
                        $"'{environment.Name}' has no host root path configured -- fill it in on the project's Edit dialog first.",
                        "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var tempDir = Path.Combine(Path.GetTempPath(), "PublishTool", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                string? downloadedZipPath = null;
                try
                {
                    // This row's build only exists as an archive on the dev server (loaded while
                    // remote mode was on) -- download it first so it can be deployed to THIS
                    // machine's Local IIS. The reverse (local artifact, remote target) can't happen:
                    // Remote never appears as a target unless remote mode is already on, in which
                    // case every row is a remote one -- see GetAvailableDeployTargets.
                    var zipPathToExtract = row.ZipPath;
                    if (zipPathToExtract is null)
                    {
                        if (row.RemoteZipPath is null || !IsRemoteModeActive(out var remoteSettings))
                        {
                            MessageBox.Show(
                                "Couldn't determine where to download this build's zip from.",
                                "PublishTool", MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }

                        downloadedZipPath = Path.Combine(Path.GetTempPath(), "PublishTool", $"{Guid.NewGuid():N}.zip");
                        _output.Stage("Downloading build from dev server...");
                        await new RemoteHostingClient().DownloadAsync(
                            remoteSettings.RemoteHostingUrl!, DecryptRemoteHostingApiKey(remoteSettings), row.RemoteZipPath, downloadedZipPath);
                        zipPathToExtract = downloadedZipPath;
                    }

                    await Task.Run(() => System.IO.Compression.ZipFile.ExtractToDirectory(zipPathToExtract, tempDir));

                    var siteName = environment.ResolveSiteName(project.Name);
                    await new BuildDeployer(_output).DeployAsync(
                        siteName, hostPath, environment.Bindings, environment.AutoCreateSite, tempDir,
                        new SiteDeploymentRecord
                        {
                            SiteName = siteName,
                            ProjectName = project.Name,
                            Version = row.Version,
                            EnvironmentName = environment.Name,
                            DeployedAtUtc = DateTimeOffset.UtcNow,
                            DeployedBy = Environment.UserName,
                        });
                    _output.Info($"Deployed {project.Name} v{row.Version} to local IIS ({environment.Name}).");
                }
                finally
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, recursive: true);
                    }

                    if (downloadedZipPath is not null && File.Exists(downloadedZipPath))
                    {
                        File.Delete(downloadedZipPath);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Deploy failed: {ex.Message}", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            BuildHistoryDataGrid.IsEnabled = true;
        }
    }

    private async void MarkBuildLatestButton_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not BuildHistoryRow row ||
            RegisteredProjectsListBox.SelectedItem is not ProjectConfig project)
        {
            return;
        }

        try
        {
            if (row.IsRemote)
            {
                if (!IsRemoteModeActive(out var settings))
                {
                    return;
                }

                await new RemoteHostingClient().UpdateAsync(
                    settings.RemoteHostingUrl!, DecryptRemoteHostingApiKey(settings), row.RemoteManifestPath!, listInHosting: null, isLatest: true);
            }
            else
            {
                var appSettings = AppSettings.Load(AppSettings.DefaultPath);
                await Task.Run(() => new BuildRepository().SetLatest(appSettings.BuildsRoot, project.Name, row.ManifestPath!));
            }

            _output.Info($"Marked {project.Name} v{row.Version} as latest.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't mark as latest: {ex.Message}", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        await LoadBuildHistoryAsync();
    }

    private async void DeleteBuildButton_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not BuildHistoryRow row ||
            RegisteredProjectsListBox.SelectedItem is not ProjectConfig project)
        {
            return;
        }

        var confirm = MessageBox.Show(
            $"Delete {project.Name} v{row.Version}? This permanently removes its archived zip and release notes.",
            "PublishTool", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            if (row.IsRemote)
            {
                if (!IsRemoteModeActive(out var settings))
                {
                    return;
                }

                await new RemoteHostingClient().DeleteAsync(settings.RemoteHostingUrl!, DecryptRemoteHostingApiKey(settings), row.RemoteManifestPath!);
            }
            else
            {
                await Task.Run(() => new BuildRepository().DeleteBuild(row.ManifestPath!));
            }

            _output.Info($"Deleted {project.Name} v{row.Version}.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Delete failed: {ex.Message}", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        await LoadBuildHistoryAsync();
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

        var project = await ProjectRegistryFactory.Create().GetAsync(projectName);
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
                "in the Projects tab first, then save.";
            return;
        }

        EventLogStatusTextBlock.Text = "Loading...";
        RefreshEventLogButton.IsEnabled = false;

        try
        {
            IReadOnlyList<EventLogEntryRecord> records;

            if (IsRemoteModeActive(out var settings))
            {
                // Reads through the dev server's own /api/eventlog instead of a direct
                // EventLogSession -- no credential prompt needed, Hosting reads its own local log
                // using the project's shared EventLog* settings.
                records = await new RemoteHostingClient().GetEventLogAsync(settings.RemoteHostingUrl!, DecryptRemoteHostingApiKey(settings), project.Name);
            }
            else
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
                records = await Task.Run(() => reader.GetRecent(options));
            }

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
            await ProjectRegistryFactory.Create().AddOrUpdateAsync(project);
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
            await RefreshProjectsAsync();
        }
    }

    private void SetBusy(bool busy)
    {
        PublishProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusTextBlock.Text = busy ? "Working..." : "Idle";

        PublishButton.IsEnabled = !busy;
        AddProjectButton.IsEnabled = !busy;
        EditProjectButton.IsEnabled = !busy;
        RemoveProjectButton.IsEnabled = !busy;
        RunCommandButton.IsEnabled = !busy;
        RefreshIisButton.IsEnabled = !busy;
        CheckoutBranchButton.IsEnabled = !busy;
    }
}
