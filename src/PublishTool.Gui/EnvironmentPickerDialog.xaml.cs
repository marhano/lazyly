using System.Windows;

namespace PublishTool.Gui;

/// <summary>Asks which target (Local/Remote) and environment to deploy a specific build to, shown
/// from the Projects tab's "Deploy this version" action -- mirrors the Publish tab's "Deploy
/// target"/"Deploy to" two-stage selector. The target selector is hidden (and skipped) when only
/// one target is available; not shown at all (dialog never opens) when there's exactly one target
/// with exactly one environment -- that one is used directly instead of making the user pick the
/// only option.</summary>
public partial class EnvironmentPickerDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _environmentsByTarget;

    public EnvironmentPickerDialog(string projectName, string version, IReadOnlyDictionary<string, IReadOnlyList<string>> environmentsByTarget)
    {
        InitializeComponent();
        _environmentsByTarget = environmentsByTarget;
        MessageTextBlock.Text = $"Deploy {projectName} v{version} to which environment?";

        if (environmentsByTarget.Count > 1)
        {
            DeployTargetPanel.Visibility = Visibility.Visible;
            DeployTargetComboBox.ItemsSource = environmentsByTarget.Keys.ToList();
            DeployTargetComboBox.SelectedIndex = 0;
        }
        else
        {
            DeployTargetPanel.Visibility = Visibility.Collapsed;
            SelectedTarget = environmentsByTarget.Keys.First();
            PopulateEnvironments(SelectedTarget);
        }
    }

    public string? SelectedTarget { get; private set; }

    public string? SelectedEnvironment => EnvironmentComboBox.SelectedItem as string;

    private void DeployTargetComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DeployTargetComboBox.SelectedItem is not string target)
        {
            return;
        }

        SelectedTarget = target;
        PopulateEnvironments(target);
    }

    private void PopulateEnvironments(string target)
    {
        var names = _environmentsByTarget.TryGetValue(target, out var value) ? value : Array.Empty<string>();
        EnvironmentComboBox.ItemsSource = names;
        EnvironmentComboBox.SelectedIndex = names.Count > 0 ? 0 : -1;
    }

    private void DeployButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTarget is null || SelectedEnvironment is null)
        {
            MessageBox.Show("Select an environment.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
