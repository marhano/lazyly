using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PublishTool.Core;
using PublishTool.Core.Models;
using PublishTool.Core.Services;
using PublishTool.Core.Services.AppConfig;

namespace PublishTool.Gui;

/// <summary>
/// Add/edit a project -- replaces the old always-visible Add Project tab form. Local fields (this
/// machine's paths, per-user local environments) are always editable; shared fields (everything
/// that syncs to the dev server in remote mode, including dev-server environments) start disabled
/// when editing an existing project with remote mode on, requiring an explicit "Edit shared
/// settings" confirmation first, since changing them affects every PublishTool user on the team.
/// </summary>
public partial class ProjectEditDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly ProjectConfig? _existing;
    private readonly ObservableCollection<DeploymentEnvironment> _localEnvironments = new();
    private readonly ObservableCollection<DeploymentEnvironment> _remoteEnvironments = new();
    private List<string> _environmentNames = new();
    private string? _defaultEnvironmentName;

    /// <summary>Every control that maps to a shared (team-wide) field -- locked together behind
    /// "Edit shared settings" when editing an existing project in remote mode. Deliberately a list
    /// of individual controls rather than one wrapping panel's IsEnabled: a shared control
    /// (AppConfigTypeComboBox) sits visually next to a local one that must stay editable regardless
    /// (AppConfigPathTextBox), and WPF's IsEnabled cascades to every descendant, so a coarse
    /// parent-level disable would have locked that local control too. RemoteEnvironmentsPanel is
    /// safe to lock as a whole -- everything inside it is shared, nothing local is nested there.</summary>
    private IEnumerable<UIElement> SharedControls => new UIElement[]
    {
        ProjectIdTextBox, PubxmlTextBox, ExtraTargetsTextBox,
        SdkStyleProjectToggle, ListInHostingToggle,
        UseAppConfigToggle, AppConfigTypeComboBox,
        UseEventLogToggle, EventLogPanel,
        RemoteEnvironmentsPanel,
    };

    public ProjectEditDialog(ProjectConfig? existing, bool remoteMode)
    {
        InitializeComponent();
        _existing = existing;

        LocalEnvironmentsDataGrid.ItemsSource = _localEnvironments;
        RemoteEnvironmentsDataGrid.ItemsSource = _remoteEnvironments;
        AppConfigTypeComboBox.ItemsSource = AppConfigProviderRegistry.All;

        TitleTextBlock.Text = existing is null ? "Add project" : $"Edit {existing.Name}";

        if (existing is not null)
        {
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
            LocalEnvironmentNameColumn.ItemsSource = _environmentNames;
            RemoteEnvironmentNameColumn.ItemsSource = _environmentNames;
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
        CsprojTextBox.Text = p.CsprojPath;
        AssemblyInfoTextBox.Text = p.AssemblyInfoPath ?? string.Empty;

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
        PubxmlTextBox.Text = p.PubxmlName;
        ExtraTargetsTextBox.Text = p.ExtraPublishTargets ?? string.Empty;
        SdkStyleProjectToggle.IsChecked = p.SdkStyleProject;
        ListInHostingToggle.IsChecked = p.ListInHosting;

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

    private void LocalIisToggle_Toggled(object sender, RoutedEventArgs e) =>
        LocalEnvironmentsSectionPanel.Visibility = LocalIisToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void RemoteIisToggle_Toggled(object sender, RoutedEventArgs e) =>
        RemoteEnvironmentsSectionPanel.Visibility = RemoteIisToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private DeploymentEnvironment NewEnvironment() =>
        new() { Name = _defaultEnvironmentName ?? _environmentNames.FirstOrDefault() ?? "Environment" };

    private void AddLocalEnvironmentButton_Click(object sender, RoutedEventArgs e) => _localEnvironments.Add(NewEnvironment());

    private void RemoveLocalEnvironmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (LocalEnvironmentsDataGrid.SelectedItem is DeploymentEnvironment env)
        {
            _localEnvironments.Remove(env);
            LocalEnvironmentBindingsDataGrid.ItemsSource = null;
        }
    }

    private void LocalEnvironmentsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        LocalEnvironmentBindingsDataGrid.ItemsSource = LocalEnvironmentsDataGrid.SelectedItem is DeploymentEnvironment env ? env.Bindings : null;

    private void AddLocalBindingButton_Click(object sender, RoutedEventArgs e)
    {
        if (LocalEnvironmentsDataGrid.SelectedItem is not DeploymentEnvironment env)
        {
            MessageBox.Show("Select a local environment first.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        env.Bindings.Add(new IisBinding { Protocol = "http", IpAddress = "*", Port = 80, HostName = null });
        LocalEnvironmentBindingsDataGrid.ItemsSource = null;
        LocalEnvironmentBindingsDataGrid.ItemsSource = env.Bindings;
    }

    private void RemoveLocalBindingButton_Click(object sender, RoutedEventArgs e)
    {
        if (LocalEnvironmentsDataGrid.SelectedItem is not DeploymentEnvironment env)
        {
            return;
        }

        if (LocalEnvironmentBindingsDataGrid.SelectedItem is IisBinding binding)
        {
            env.Bindings.Remove(binding);
            LocalEnvironmentBindingsDataGrid.ItemsSource = null;
            LocalEnvironmentBindingsDataGrid.ItemsSource = env.Bindings;
        }
    }

    private void AddRemoteEnvironmentButton_Click(object sender, RoutedEventArgs e) => _remoteEnvironments.Add(NewEnvironment());

    private void RemoveRemoteEnvironmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (RemoteEnvironmentsDataGrid.SelectedItem is DeploymentEnvironment env)
        {
            _remoteEnvironments.Remove(env);
            RemoteEnvironmentBindingsDataGrid.ItemsSource = null;
        }
    }

    private void RemoteEnvironmentsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RemoteEnvironmentBindingsDataGrid.ItemsSource = RemoteEnvironmentsDataGrid.SelectedItem is DeploymentEnvironment env ? env.Bindings : null;

    private void AddRemoteBindingButton_Click(object sender, RoutedEventArgs e)
    {
        if (RemoteEnvironmentsDataGrid.SelectedItem is not DeploymentEnvironment env)
        {
            MessageBox.Show("Select a dev-server environment first.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        env.Bindings.Add(new IisBinding { Protocol = "http", IpAddress = "*", Port = 80, HostName = null });
        RemoteEnvironmentBindingsDataGrid.ItemsSource = null;
        RemoteEnvironmentBindingsDataGrid.ItemsSource = env.Bindings;
    }

    private void RemoveRemoteBindingButton_Click(object sender, RoutedEventArgs e)
    {
        if (RemoteEnvironmentsDataGrid.SelectedItem is not DeploymentEnvironment env)
        {
            return;
        }

        if (RemoteEnvironmentBindingsDataGrid.SelectedItem is IisBinding binding)
        {
            env.Bindings.Remove(binding);
            RemoteEnvironmentBindingsDataGrid.ItemsSource = null;
            RemoteEnvironmentBindingsDataGrid.ItemsSource = env.Bindings;
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
        if (string.IsNullOrWhiteSpace(NameTextBox.Text) ||
            string.IsNullOrWhiteSpace(CsprojTextBox.Text) ||
            string.IsNullOrWhiteSpace(PubxmlTextBox.Text))
        {
            MessageBox.Show(
                "Name, .csproj path, and publish profile are required.",
                "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

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

        var useAppConfig = UseAppConfigToggle.IsChecked == true;
        if (useAppConfig && (AppConfigTypeComboBox.SelectedItem is not IAppConfigProvider || string.IsNullOrWhiteSpace(AppConfigPathTextBox.Text)))
        {
            MessageBox.Show(
                "App config editing is on but the config type or file path is missing. Fill both in, or turn the toggle off.",
                "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var useEventLog = UseEventLogToggle.IsChecked == true;
        if (useEventLog && string.IsNullOrWhiteSpace(EventLogFilterValueTextBox.Text))
        {
            MessageBox.Show(
                "Event Logs is on but no Source name or message text filter was entered. Fill it in, or turn the toggle off.",
                "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var filterType = (EventLogFilterTypeComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? EventLogFilterTypes.Source;
        var appConfigProvider = AppConfigTypeComboBox.SelectedItem as IAppConfigProvider;

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

        var config = new ProjectConfig
        {
            Name = NameTextBox.Text.Trim(),
            ProjectId = string.IsNullOrWhiteSpace(ProjectIdTextBox.Text) ? null : ProjectIdTextBox.Text.Trim(),
            LastReleaseNotesSequence = _existing?.LastReleaseNotesSequence ?? 0,
            CsprojPath = CsprojTextBox.Text.Trim(),
            PubxmlName = PubxmlTextBox.Text.Trim(),
            AssemblyInfoPath = string.IsNullOrWhiteSpace(AssemblyInfoTextBox.Text) ? null : AssemblyInfoTextBox.Text.Trim(),
            ExtraPublishTargets = string.IsNullOrWhiteSpace(ExtraTargetsTextBox.Text) ? null : ExtraTargetsTextBox.Text.Trim(),
            LocalIisEnabled = localIisEnabled,
            LocalEnvironments = _localEnvironments.ToList(),
            SdkStyleProject = SdkStyleProjectToggle.IsChecked == true,
            ListInHosting = ListInHostingToggle.IsChecked == true,
            UseAppConfig = useAppConfig,
            AppConfigType = useAppConfig ? appConfigProvider?.TypeName : null,
            AppConfigPath = useAppConfig ? AppConfigPathTextBox.Text.Trim() : null,
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
