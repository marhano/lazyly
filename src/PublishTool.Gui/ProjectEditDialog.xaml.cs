using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PublishTool.Core;
using PublishTool.Core.Models;
using PublishTool.Core.Services;
using PublishTool.Core.Services.AppConfig;
using PublishTool.Core.Services.BuildRunners;

namespace PublishTool.Gui;

/// <summary>
/// Add/edit a project -- replaces the old always-visible Add Project tab form. Local fields (this
/// machine's paths, per-user local environments) are always editable; shared fields (everything
/// that syncs to the dev server in remote mode, including dev-server environments) start disabled
/// when editing an existing project with remote mode on, requiring an explicit "Edit shared
/// settings" confirmation first, since changing them affects every PublishTool user on the team.
/// Every field except Name is optional -- a project can be registered purely to manage an existing
/// build's IIS site, Event Log, or firewall rules, with nothing else filled in.
/// </summary>
public partial class ProjectEditDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly ProjectConfig? _existing;

    /// <summary>The saved project's name, set right before a successful save closes the dialog --
    /// lets the caller log which project was added/edited without duplicating this dialog's own
    /// field-reading logic.</summary>
    public string? SavedProjectName { get; private set; }

    private readonly ObservableCollection<DeploymentEnvironment> _localEnvironments = new();
    private readonly ObservableCollection<DeploymentEnvironment> _remoteEnvironments = new();
    private List<string> _environmentNames = new();
    private string? _defaultEnvironmentName;

    // Held in memory (not bound to a visible control) until Save persists them protected --
    // AndroidSigningDialog is the only place these are actually typed/shown.
    private string? _androidKeystorePath;
    private string? _androidKeyAlias;
    private string? _androidPlainKeystorePassword;
    private string? _androidPlainKeyPassword;

    /// <summary>Every control that maps to a shared (team-wide) field -- locked together behind
    /// "Edit shared settings" when editing an existing project in remote mode. Deliberately a list
    /// of individual controls rather than one wrapping panel's IsEnabled: a shared control
    /// (AppConfigTypeComboBox) sits visually next to a local one that must stay editable regardless
    /// (AppConfigPathTextBox), and WPF's IsEnabled cascades to every descendant, so a coarse
    /// parent-level disable would have locked that local control too. RemoteEnvironmentsSectionPanel
    /// is safe to lock as a whole -- everything inside it (the host root path box included) is
    /// shared, nothing local is nested there. Note this locks the section's *contents*, not the
    /// RemoteIisToggle above it that reveals the section -- that toggle is deliberately per-user
    /// (each dev decides independently whether they use Remote IIS for this project at all), even
    /// though the section it reveals is shared team data once you're looking at it. NameTextBox is
    /// deliberately NOT here -- it gets a stronger, permanent lock (see the constructor) that even
    /// "Edit shared settings" can't undo, since renaming a project would orphan its build folder and
    /// any shared registration under the old name.</summary>
    private IEnumerable<UIElement> SharedControls => new UIElement[]
    {
        ProjectIdTextBox, ProjectTypeComboBox,
        DotNetSharedFieldsPanel, AngularSharedFieldsPanel,
        UseAppConfigToggle, AppConfigTypeComboBox,
        UseEventLogToggle, EventLogPanel,
        RemoteEnvironmentsSectionPanel,
    };

    public ProjectEditDialog(ProjectConfig? existing, bool remoteMode)
    {
        InitializeComponent();
        _existing = existing;

        LocalEnvironmentsDataGrid.ItemsSource = _localEnvironments;
        RemoteEnvironmentsDataGrid.ItemsSource = _remoteEnvironments;

        TitleTextBlock.Text = existing is null ? "Add project" : $"Edit {existing.Name}";

        // Defaults to .NET (index 0) for a brand-new project; PopulateFrom below overwrites this
        // for an existing one. Set programmatically (not via XAML SelectedIndex) so the
        // SelectionChanged handler it fires runs after every other named control already exists --
        // a XAML-time SelectedIndex fires during InitializeComponent, before later sibling controls
        // are constructed yet.
        ProjectTypeComboBox.SelectedIndex = 0;
        UpdateAndroidSigningStatusText();

        if (existing is not null)
        {
            // Permanent, regardless of remote mode or "Edit shared settings" -- the project name is
            // the key its build folder and any shared registration are filed under, so changing it
            // after the fact would orphan both rather than rename them.
            NameTextBox.IsEnabled = false;
            PopulateFrom(existing);
        }

        // The edit-safety gate only makes sense for an EXISTING project while remote mode is on --
        // a brand-new project has nothing shared yet to protect, and in local mode "shared" doesn't
        // mean anything (the whole file is just this dev's own).
        if (existing is not null && remoteMode)
        {
            foreach (var control in SharedControls)
            {
                control.IsEnabled = false;
            }

            EditSharedSettingsButton.Visibility = Visibility.Visible;
        }

        // Loading the environment name list can mean an HTTP call (remote mode) -- defer to Loaded
        // instead of blocking the constructor.
        Loaded += async (_, _) => await LoadEnvironmentNamesAsync();
    }

    private async Task LoadEnvironmentNamesAsync()
    {
        try
        {
            var settings = await EnvironmentRegistryFactory.Create().GetAsync();
            _environmentNames = settings.Names;
            _defaultEnvironmentName = settings.DefaultName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Couldn't load the deployment environment list: {ex.Message}",
                "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PopulateFrom(ProjectConfig p)
    {
        NameTextBox.Text = p.Name;
        CsprojTextBox.Text = p.CsprojPath ?? string.Empty;
        AssemblyInfoTextBox.Text = p.AssemblyInfoPath ?? string.Empty;
        PubxmlTextBox.Text = p.PubxmlName ?? string.Empty;

        foreach (var env in p.LocalEnvironments)
        {
            _localEnvironments.Add(env);
        }

        // One shared root for every local environment -- they should all already agree (SaveButton_Click
        // enforces it going forward), so the first non-empty value stands in for "the" root.
        LocalHostRootPathTextBox.Text = p.LocalEnvironments.FirstOrDefault(env => !string.IsNullOrWhiteSpace(env.HostRootPath))?.HostRootPath ?? string.Empty;

        LocalIisToggle.IsChecked = p.LocalIisEnabled;
        LocalEnvironmentsSectionPanel.Visibility = p.LocalIisEnabled ? Visibility.Visible : Visibility.Collapsed;

        ProjectIdTextBox.Text = p.ProjectId ?? string.Empty;
        // Fires ProjectTypeComboBox_SelectionChanged, which toggles every type-specific panel's
        // visibility -- set before populating this project's type-specific fields below.
        ProjectTypeComboBox.SelectedIndex = p.ProjectType switch { ProjectType.Angular => 1, ProjectType.Android => 2, _ => 0 };

        ExtraTargetsTextBox.Text = p.ExtraPublishTargets ?? string.Empty;
        SdkStyleProjectToggle.IsChecked = p.SdkStyleProject;

        ProjectRootTextBox.Text = p.ProjectType switch
        {
            ProjectType.Angular => p.Angular?.ProjectRootPath ?? string.Empty,
            ProjectType.Android => p.Android?.ProjectRootPath ?? string.Empty,
            _ => string.Empty,
        };
        AngularWorkspaceProjectTextBox.Text = p.Angular?.WorkspaceProjectName ?? string.Empty;

        UpdateAndroidDetectedLabel();

        _androidKeystorePath = p.Android?.KeystorePath;
        _androidKeyAlias = p.Android?.KeyAlias;
        _androidPlainKeystorePassword = p.Android?.ProtectedKeystorePassword is { } protectedKeystorePassword
            ? SecretProtector.TryUnprotect(protectedKeystorePassword, SecretProtector.AndroidSigningPurpose)
            : null;
        _androidPlainKeyPassword = p.Android?.ProtectedKeyPassword is { } protectedKeyPassword
            ? SecretProtector.TryUnprotect(protectedKeyPassword, SecretProtector.AndroidSigningPurpose)
            : null;
        UpdateAndroidSigningStatusText();

        UseAppConfigToggle.IsChecked = p.UseAppConfig;
        AppConfigTypePanel.Visibility = p.UseAppConfig ? Visibility.Visible : Visibility.Collapsed;
        AppConfigTypeComboBox.SelectedItem = AppConfigProviderRegistry.Get(p.AppConfigType);
        AppConfigPathTextBox.Text = p.AppConfigPath ?? string.Empty;

        UseEventLogToggle.IsChecked = p.UseEventLog;
        EventLogPanel.Visibility = p.UseEventLog ? Visibility.Visible : Visibility.Collapsed;
        EventLogNameTextBox.Text = p.EventLogName ?? "Application";
        EventLogFilterTypeComboBox.SelectedIndex =
            string.Equals(p.EventLogFilterType, EventLogFilterTypes.MessageContains, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        EventLogFilterValueTextBox.Text = p.EventLogFilterValue ?? string.Empty;
        EventLogMachineTextBox.Text = p.EventLogMachineName ?? string.Empty;
        EventLogUsernameTextBox.Text = p.EventLogUsername ?? string.Empty;

        RemoteIisToggle.IsChecked = p.RemoteIisEnabled;
        RemoteEnvironmentsSectionPanel.Visibility = p.RemoteIisEnabled ? Visibility.Visible : Visibility.Collapsed;

        foreach (var env in p.RemoteEnvironments)
        {
            _remoteEnvironments.Add(env);
        }

        // One shared root for every dev-server environment, same reasoning as LocalHostRootPathTextBox.
        RemoteHostRootPathTextBox.Text = p.RemoteEnvironments.FirstOrDefault(env => !string.IsNullOrWhiteSpace(env.HostRootPath))?.HostRootPath ?? string.Empty;
    }

    private void BrowseCsproj_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Project files (*.csproj)|*.csproj|All files (*.*)|*.*" };
        if (dialog.ShowDialog() == true)
        {
            CsprojTextBox.Text = dialog.FileName;
        }
    }

    private void BrowseAssemblyInfo_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "C# files (*.cs)|*.cs|All files (*.*)|*.*" };
        if (dialog.ShowDialog() == true)
        {
            AssemblyInfoTextBox.Text = dialog.FileName;
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

    private void BrowseLocalHostRootPath_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog();
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            LocalHostRootPathTextBox.Text = dialog.SelectedPath;
        }
    }

    private void BrowseProjectRoot_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog();
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            ProjectRootTextBox.Text = dialog.SelectedPath;
            UpdateAndroidDetectedLabel();
        }
    }

    private void ProjectRootTextBox_LostFocus(object sender, RoutedEventArgs e) => UpdateAndroidDetectedLabel();

    /// <summary>Informational only, never persisted -- recomputed from the folder's actual contents
    /// every time it might have changed, so it can never go stale the way a stored wrapper-type
    /// field could. Only shown (see ProjectTypeComboBox_SelectionChanged) while Android is the
    /// selected project type, but harmless to keep up to date regardless.</summary>
    private void UpdateAndroidDetectedLabel()
    {
        var path = ProjectRootTextBox.Text;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            AndroidDetectedLabel.Text = "(point at a project folder above)";
            return;
        }

        var wrapper = AndroidWrapperStrategyRegistry.Detect(path);
        AndroidDetectedLabel.Text = wrapper is null
            ? "Not recognized -- expected a capacitor.config.json/.ts or config.xml file in this folder."
            : $"{wrapper.DisplayName} project";
    }

    private void ConfigureAndroidSigningButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AndroidSigningDialog(_androidKeystorePath, _androidKeyAlias, _androidPlainKeystorePassword, _androidPlainKeyPassword) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (dialog.WasCleared)
        {
            _androidKeystorePath = null;
            _androidKeyAlias = null;
            _androidPlainKeystorePassword = null;
            _androidPlainKeyPassword = null;
        }
        else
        {
            _androidKeystorePath = dialog.KeystorePath;
            _androidKeyAlias = dialog.KeyAlias;
            _androidPlainKeystorePassword = dialog.KeystorePassword;
            _androidPlainKeyPassword = dialog.KeyPassword;
        }

        UpdateAndroidSigningStatusText();
    }

    private void UpdateAndroidSigningStatusText() => AndroidSigningStatusTextBlock.Text = _androidKeystorePath is null
        ? "Not configured -- release builds use the native project's own signingConfig, if any."
        : $"{Path.GetFileName(_androidKeystorePath)} (alias: {_androidKeyAlias})";

    private void ProjectTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var tag = (ProjectTypeComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "DotNet";
        var projectType = tag switch { "Angular" => ProjectType.Angular, "Android" => ProjectType.Android, _ => ProjectType.DotNet };

        DotNetLocalFieldsGrid.Visibility = tag == "DotNet" ? Visibility.Visible : Visibility.Collapsed;
        DotNetSharedFieldsPanel.Visibility = tag == "DotNet" ? Visibility.Visible : Visibility.Collapsed;

        ProjectRootFieldsGrid.Visibility = tag is "Angular" or "Android" ? Visibility.Visible : Visibility.Collapsed;
        AngularSharedFieldsPanel.Visibility = tag == "Angular" ? Visibility.Visible : Visibility.Collapsed;

        AndroidDetectedCaptionText.Visibility = tag == "Android" ? Visibility.Visible : Visibility.Collapsed;
        AndroidDetectedLabel.Visibility = tag == "Android" ? Visibility.Visible : Visibility.Collapsed;
        AndroidSigningSectionPanel.Visibility = tag == "Android" ? Visibility.Visible : Visibility.Collapsed;

        // Android has no IIS deploy story at all (see BuildRunners/AndroidBuildRunner) -- hide Local
        // IIS entirely for it rather than leave a toggle that could never do anything.
        var localIisApplicable = tag != "Android";
        LocalIisToggle.Visibility = localIisApplicable ? Visibility.Visible : Visibility.Collapsed;
        LocalEnvironmentsSectionPanel.Visibility = localIisApplicable && LocalIisToggle.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;

        UpdateAppConfigTypeOptions(projectType);
    }

    /// <summary>Only offers config formats that actually make sense for the selected project type
    /// (Web.config/App.config/appsettings.json for .NET, an Angular-style environment.ts for
    /// Angular/Android) instead of listing every registered format regardless of relevance.
    /// Preserves the current selection across the change if it's still valid for the new type.</summary>
    private void UpdateAppConfigTypeOptions(ProjectType projectType)
    {
        var previousSelection = AppConfigTypeComboBox.SelectedItem as IAppConfigProvider;
        var applicable = AppConfigProviderRegistry.All.Where(p => p.ApplicableProjectTypes.Contains(projectType)).ToList();
        AppConfigTypeComboBox.ItemsSource = applicable;
        AppConfigTypeComboBox.SelectedItem = previousSelection is not null && applicable.Contains(previousSelection) ? previousSelection : null;
    }

    private void LocalIisToggle_Toggled(object sender, RoutedEventArgs e) =>
        LocalEnvironmentsSectionPanel.Visibility = LocalIisToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void RemoteIisToggle_Toggled(object sender, RoutedEventArgs e) =>
        RemoteEnvironmentsSectionPanel.Visibility = RemoteIisToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void AddLocalEnvironmentButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new EditEnvironmentDialog(null, _environmentNames, _defaultEnvironmentName) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _localEnvironments.Add(dialog.Result);
        }
    }

    private void EditLocalEnvironmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not DeploymentEnvironment env)
        {
            return;
        }

        var dialog = new EditEnvironmentDialog(env, _environmentNames, _defaultEnvironmentName) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var index = _localEnvironments.IndexOf(env);
        if (index >= 0)
        {
            _localEnvironments[index] = dialog.Result;
        }
    }

    private void RemoveLocalEnvironmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is DeploymentEnvironment env)
        {
            _localEnvironments.Remove(env);
        }
    }

    private void AddRemoteEnvironmentButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new EditEnvironmentDialog(null, _environmentNames, _defaultEnvironmentName) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _remoteEnvironments.Add(dialog.Result);
        }
    }

    private void EditRemoteEnvironmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not DeploymentEnvironment env)
        {
            return;
        }

        var dialog = new EditEnvironmentDialog(env, _environmentNames, _defaultEnvironmentName) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var index = _remoteEnvironments.IndexOf(env);
        if (index >= 0)
        {
            _remoteEnvironments[index] = dialog.Result;
        }
    }

    private void RemoveRemoteEnvironmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is DeploymentEnvironment env)
        {
            _remoteEnvironments.Remove(env);
        }
    }

    private void UseAppConfigToggle_Toggled(object sender, RoutedEventArgs e)
    {
        var isOn = UseAppConfigToggle.IsChecked == true;
        AppConfigTypePanel.Visibility = isOn ? Visibility.Visible : Visibility.Collapsed;
        if (isOn && AppConfigTypeComboBox.SelectedItem is null && AppConfigTypeComboBox.Items.Count > 0)
        {
            AppConfigTypeComboBox.SelectedIndex = 0;
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

    private void EditSharedSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "These settings are shared with your whole team via the dev server -- changing them affects every PublishTool user. Continue?",
            "PublishTool", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        foreach (var control in SharedControls)
        {
            control.IsEnabled = true;
        }

        EditSharedSettingsButton.Visibility = Visibility.Collapsed;
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameTextBox.Text))
        {
            MessageBox.Show("Name is required.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var projectTypeTag = (ProjectTypeComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "DotNet";
        var projectType = projectTypeTag switch
        {
            "Angular" => ProjectType.Angular,
            "Android" => ProjectType.Android,
            _ => ProjectType.DotNet,
        };

        // Every other field is deliberately optional -- a project can be registered purely to
        // manage an existing build's IIS site, Event Log, or firewall rules, with nothing else
        // filled in. Publisher/the relevant build runner is what actually enforces what it needs
        // at publish time, with a clear error naming what's missing.

        var localIisEnabled = LocalIisToggle.IsChecked == true;
        var remoteIisEnabled = RemoteIisToggle.IsChecked == true;

        if (localIisEnabled && !ValidateEnvironments(_localEnvironments, "local"))
        {
            return;
        }

        if (remoteIisEnabled && !ValidateEnvironments(_remoteEnvironments, "dev-server"))
        {
            return;
        }

        // Config file path is optional even with app config on -- left blank, PublishTool looks
        // for one automatically under the project's source root at publish time. Only the type is
        // actually required, since that's what says which format/search pattern to use.
        var useAppConfig = UseAppConfigToggle.IsChecked == true;
        if (useAppConfig && AppConfigTypeComboBox.SelectedItem is not IAppConfigProvider)
        {
            MessageBox.Show(
                "App config editing is on but no config type is selected. Pick one, or turn the toggle off.",
                "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var appConfigProvider = AppConfigTypeComboBox.SelectedItem as IAppConfigProvider;

        var useEventLog = UseEventLogToggle.IsChecked == true;
        if (useEventLog && string.IsNullOrWhiteSpace(EventLogFilterValueTextBox.Text))
        {
            MessageBox.Show(
                "Event Logs is on but no Source name or message text filter was entered. Fill it in, or turn the toggle off.",
                "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var filterType = (EventLogFilterTypeComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? EventLogFilterTypes.Source;

        // One shared root for every local (resp. dev-server) environment, not a per-row setting --
        // see LocalHostRootPathTextBox/RemoteHostRootPathTextBox.
        var localHostRootPath = string.IsNullOrWhiteSpace(LocalHostRootPathTextBox.Text) ? null : LocalHostRootPathTextBox.Text.Trim();
        foreach (var env in _localEnvironments)
        {
            env.HostRootPath = localHostRootPath;
        }

        var remoteHostRootPath = string.IsNullOrWhiteSpace(RemoteHostRootPathTextBox.Text) ? null : RemoteHostRootPathTextBox.Text.Trim();
        foreach (var env in _remoteEnvironments)
        {
            env.HostRootPath = remoteHostRootPath;
        }

        var projectRootPath = string.IsNullOrWhiteSpace(ProjectRootTextBox.Text) ? null : ProjectRootTextBox.Text.Trim();

        var config = new ProjectConfig
        {
            Name = NameTextBox.Text.Trim(),
            ProjectId = string.IsNullOrWhiteSpace(ProjectIdTextBox.Text) ? null : ProjectIdTextBox.Text.Trim(),
            LastReleaseNotesSequence = _existing?.LastReleaseNotesSequence ?? 0,
            ProjectType = projectType,
            CsprojPath = projectType == ProjectType.DotNet && !string.IsNullOrWhiteSpace(CsprojTextBox.Text) ? CsprojTextBox.Text.Trim() : null,
            PubxmlName = projectType == ProjectType.DotNet && !string.IsNullOrWhiteSpace(PubxmlTextBox.Text) ? PubxmlTextBox.Text.Trim() : null,
            AssemblyInfoPath = projectType == ProjectType.DotNet && !string.IsNullOrWhiteSpace(AssemblyInfoTextBox.Text) ? AssemblyInfoTextBox.Text.Trim() : null,
            ExtraPublishTargets = projectType == ProjectType.DotNet && !string.IsNullOrWhiteSpace(ExtraTargetsTextBox.Text) ? ExtraTargetsTextBox.Text.Trim() : null,
            SdkStyleProject = projectType == ProjectType.DotNet && SdkStyleProjectToggle.IsChecked == true,
            Angular = projectType == ProjectType.Angular ? new AngularProjectSettings
            {
                ProjectRootPath = projectRootPath,
                WorkspaceProjectName = string.IsNullOrWhiteSpace(AngularWorkspaceProjectTextBox.Text) ? null : AngularWorkspaceProjectTextBox.Text.Trim(),
            } : null,
            Android = projectType == ProjectType.Android ? new AndroidProjectSettings
            {
                ProjectRootPath = projectRootPath,
                KeystorePath = _androidKeystorePath,
                KeyAlias = _androidKeyAlias,
                ProtectedKeystorePassword = _androidPlainKeystorePassword is { } keystorePasswordToSave
                    ? SecretProtector.Protect(keystorePasswordToSave, SecretProtector.AndroidSigningPurpose)
                    : null,
                ProtectedKeyPassword = _androidPlainKeyPassword is { } keyPasswordToSave
                    ? SecretProtector.Protect(keyPasswordToSave, SecretProtector.AndroidSigningPurpose)
                    : null,
            } : null,
            LocalIisEnabled = localIisEnabled,
            LocalEnvironments = _localEnvironments.ToList(),
            // No dialog control for this anymore -- it's decided per-publish on the Publish tab
            // instead (see MainWindow's ListInHostingToggle), so this just preserves whatever a
            // teammate or the CLI's add-project already set as this project's own default.
            ListInHosting = _existing?.ListInHosting ?? true,
            UseAppConfig = useAppConfig,
            AppConfigType = useAppConfig ? appConfigProvider?.TypeName : null,
            AppConfigPath = useAppConfig && !string.IsNullOrWhiteSpace(AppConfigPathTextBox.Text) ? AppConfigPathTextBox.Text.Trim() : null,
            UseEventLog = useEventLog,
            EventLogName = useEventLog ? (string.IsNullOrWhiteSpace(EventLogNameTextBox.Text) ? "Application" : EventLogNameTextBox.Text.Trim()) : "Application",
            EventLogFilterType = useEventLog ? filterType : EventLogFilterTypes.Source,
            EventLogFilterValue = useEventLog ? EventLogFilterValueTextBox.Text.Trim() : null,
            EventLogMachineName = useEventLog && !string.IsNullOrWhiteSpace(EventLogMachineTextBox.Text) ? EventLogMachineTextBox.Text.Trim() : null,
            EventLogUsername = useEventLog && !string.IsNullOrWhiteSpace(EventLogUsernameTextBox.Text) ? EventLogUsernameTextBox.Text.Trim() : null,
            EventLogProtectedPassword = _existing?.EventLogProtectedPassword,
            RemoteIisEnabled = remoteIisEnabled,
            RemoteEnvironments = _remoteEnvironments.ToList(),
        };

        try
        {
            await ProjectRegistryFactory.Create().AddOrUpdateAsync(config);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't save project: {ex.Message}", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        SavedProjectName = config.Name;
        DialogResult = true;
    }

    /// <summary>Duplicate names are ambiguous (which one would a publish deploy to?) and an
    /// auto-create site with no bindings can't actually create anything -- both block save with a
    /// message naming the offending environment.</summary>
    private bool ValidateEnvironments(IEnumerable<DeploymentEnvironment> environments, string kind)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var env in environments)
        {
            if (!seen.Add(env.Name))
            {
                MessageBox.Show(
                    $"'{env.Name}' is configured more than once under {kind} environments. Each environment can only appear once.",
                    "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (env.AutoCreateSite && env.Bindings.Count == 0)
            {
                MessageBox.Show(
                    $"'{env.Name}' ({kind}) has auto-create site on but no bindings were added. Add at least one, or turn it off.",
                    "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        return true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
