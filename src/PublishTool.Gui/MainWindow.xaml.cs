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
    private bool _isBusy;
    private bool _isExiting;

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
        ElevationInfoBar.IsOpen = !IsRunningAsAdministrator();

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
        OutputPanel.Visibility = isCurrentlyVisible ? Visibility.Collapsed : Visibility.Visible;
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

    private async void PublishButton_Click(object sender, RoutedEventArgs e)
    {
        var project = ProjectComboBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(VersionTextBox.Text))
        {
            MessageBox.Show(
                "Select a project and fill in a version.",
                "PublishTool",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var args = new List<string>
        {
            "publish",
            "--project", project,
            "--version", VersionTextBox.Text,
        };

        foreach (var item in FeaturesEditor.Items) { args.Add("--feature"); args.Add(item); }
        foreach (var item in FixesEditor.Items) { args.Add("--fix"); args.Add(item); }
        foreach (var item in OtherUpdatesEditor.Items) { args.Add("--other-update"); args.Add(item); }
        foreach (var item in BacklogItemsEditor.Items) { args.Add("--backlog-item"); args.Add(item); }

        await RunAsync(args.ToArray());
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
    }
}
