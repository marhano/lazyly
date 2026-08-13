using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Win32;
using PublishTool.Core;
using PublishTool.Core.Models;
using PublishTool.Core.Services;
using PublishTool.Core.Services.AppConfig;

namespace PublishTool.Gui;

/// <summary>
/// Add/edit a project -- replaces the old always-visible Add Project tab form. Local fields (this
/// machine's paths, per-user local/remote deploy toggles) are always editable; shared fields
/// (everything that syncs to the dev server in remote mode) start disabled when editing an existing
/// project with remote mode on, requiring an explicit "Edit shared settings" confirmation first,
/// since changing them affects every PublishTool user on the team.
/// </summary>
public partial class ProjectEditDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly ProjectConfig? _existing;
    private readonly ObservableCollection<IisBinding> _localBindings = new();
    private readonly ObservableCollection<IisBinding> _remoteBindings = new();

    /// <summary>Every control that maps to a shared (team-wide) field -- locked together behind
    /// "Edit shared settings" when editing an existing project in remote mode. Deliberately a list
    /// of individual controls rather than one wrapping panel's IsEnabled: a couple of shared
    /// controls (AppConfigTypeComboBox, RemoteIisDeployPanel's toggle) sit visually next to local
    /// controls that must stay editable regardless (AppConfigPathTextBox, AutoDeployOnPublishToggle),
    /// and WPF's IsEnabled cascades to every descendant, so a coarse parent-level disable would have
    /// locked those local controls too.</summary>
    private IEnumerable<UIElement> SharedControls => new UIElement[]
    {
        ProjectIdTextBox, PubxmlTextBox, ExtraTargetsTextBox,
        SdkStyleProjectToggle, ListInHostingToggle,
        UseAppConfigToggle, AppConfigTypeComboBox,
        UseEventLogToggle, EventLogPanel,
        RemoteIisDeployPanel,
    };

    public ProjectEditDialog(ProjectConfig? existing, bool remoteMode)
    {
        InitializeComponent();
        _existing = existing;

        LocalIisBindingsDataGrid.ItemsSource = _localBindings;
        RemoteIisBindingsDataGrid.ItemsSource = _remoteBindings;
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
    }

    private void PopulateFrom(ProjectConfig p)
    {
        NameTextBox.Text = p.Name;
        CsprojTextBox.Text = p.CsprojPath;
        AssemblyInfoTextBox.Text = p.AssemblyInfoPath ?? string.Empty;

        LocalIisDeploymentToggle.IsChecked = p.LocalIisDeploymentEnabled;
        LocalIisDeploymentPanel.Visibility = p.LocalIisDeploymentEnabled ? Visibility.Visible : Visibility.Collapsed;
        IisHostTextBox.Text = p.IisHostPath ?? string.Empty;
        AutoCreateIisSiteToggle.IsChecked = p.AutoCreateIisSite;
        foreach (var binding in p.IisBindings)
        {
            _localBindings.Add(binding);
        }

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

        AutoDeployOnPublishToggle.IsChecked = p.AutoDeployOnPublish;
        RemoteIisDeployPanel.Visibility = p.AutoDeployOnPublish ? Visibility.Visible : Visibility.Collapsed;
        RemoteIisHostTextBox.Text = p.RemoteIisHostPath ?? string.Empty;
        RemoteAutoCreateIisSiteToggle.IsChecked = p.RemoteAutoCreateIisSite;
        foreach (var binding in p.RemoteIisBindings)
        {
            _remoteBindings.Add(binding);
        }
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

    private void BrowseIisHost_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog();
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            IisHostTextBox.Text = dialog.SelectedPath;
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

    private void AddLocalBindingButton_Click(object sender, RoutedEventArgs e) =>
        _localBindings.Add(new IisBinding { Protocol = "http", IpAddress = "*", Port = 80, HostName = null });

    private void RemoveLocalBindingButton_Click(object sender, RoutedEventArgs e)
    {
        if (LocalIisBindingsDataGrid.SelectedItem is IisBinding binding)
        {
            _localBindings.Remove(binding);
        }
    }

    private void AddRemoteBindingButton_Click(object sender, RoutedEventArgs e) =>
        _remoteBindings.Add(new IisBinding { Protocol = "http", IpAddress = "*", Port = 80, HostName = null });

    private void RemoveRemoteBindingButton_Click(object sender, RoutedEventArgs e)
    {
        if (RemoteIisBindingsDataGrid.SelectedItem is IisBinding binding)
        {
            _remoteBindings.Remove(binding);
        }
    }

    private void LocalIisDeploymentToggle_Toggled(object sender, RoutedEventArgs e)
    {
        LocalIisDeploymentPanel.Visibility = LocalIisDeploymentToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AutoDeployOnPublishToggle_Toggled(object sender, RoutedEventArgs e)
    {
        RemoteIisDeployPanel.Visibility = AutoDeployOnPublishToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
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

        var localIisDeployment = LocalIisDeploymentToggle.IsChecked == true;
        if (localIisDeployment && string.IsNullOrWhiteSpace(IisHostTextBox.Text))
        {
            MessageBox.Show(
                "Local IIS Deployment is on but no host folder was entered. Fill it in, or turn the toggle off.",
                "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var autoCreateIisSite = AutoCreateIisSiteToggle.IsChecked == true;
        if (localIisDeployment && autoCreateIisSite && _localBindings.Count == 0)
        {
            MessageBox.Show(
                "Auto-create local IIS site is on but no bindings were added. Add at least one, or turn the toggle off.",
                "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var autoDeployOnPublish = AutoDeployOnPublishToggle.IsChecked == true;
        var remoteAutoCreateIisSite = RemoteAutoCreateIisSiteToggle.IsChecked == true;
        if (autoDeployOnPublish && remoteAutoCreateIisSite && _remoteBindings.Count == 0)
        {
            MessageBox.Show(
                "Auto-create dev-server IIS site is on but no bindings were added. Add at least one, or turn the toggle off.",
                "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
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

        var config = new ProjectConfig
        {
            Name = NameTextBox.Text.Trim(),
            ProjectId = string.IsNullOrWhiteSpace(ProjectIdTextBox.Text) ? null : ProjectIdTextBox.Text.Trim(),
            LastReleaseNotesSequence = _existing?.LastReleaseNotesSequence ?? 0,
            CsprojPath = CsprojTextBox.Text.Trim(),
            PubxmlName = PubxmlTextBox.Text.Trim(),
            AssemblyInfoPath = string.IsNullOrWhiteSpace(AssemblyInfoTextBox.Text) ? null : AssemblyInfoTextBox.Text.Trim(),
            LocalIisDeploymentEnabled = localIisDeployment,
            IisHostPath = string.IsNullOrWhiteSpace(IisHostTextBox.Text) ? null : IisHostTextBox.Text.Trim(),
            ExtraPublishTargets = string.IsNullOrWhiteSpace(ExtraTargetsTextBox.Text) ? null : ExtraTargetsTextBox.Text.Trim(),
            AutoCreateIisSite = autoCreateIisSite,
            IisBindings = _localBindings.ToList(),
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
            RemoteIisHostPath = string.IsNullOrWhiteSpace(RemoteIisHostTextBox.Text) ? null : RemoteIisHostTextBox.Text.Trim(),
            RemoteIisBindings = _remoteBindings.ToList(),
            RemoteAutoCreateIisSite = remoteAutoCreateIisSite,
            AutoDeployOnPublish = autoDeployOnPublish,
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

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
