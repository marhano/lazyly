using System.Windows;

namespace PublishTool.Gui;

/// <summary>Asks which configured environment to deploy a specific build to, when a project has
/// more than one -- shown from the Projects tab's "Deploy this version" action. Not needed (and not
/// shown) when a project has exactly one environment for the side being deployed to; that one is
/// used directly instead of making the user pick the only option.</summary>
public partial class EnvironmentPickerDialog : Wpf.Ui.Controls.FluentWindow
{
    public EnvironmentPickerDialog(string projectName, string version, IReadOnlyList<string> environmentNames)
    {
        InitializeComponent();
        MessageTextBlock.Text = $"Deploy {projectName} v{version} to which environment?";
        EnvironmentComboBox.ItemsSource = environmentNames;
        EnvironmentComboBox.SelectedIndex = 0;
    }

    public string? SelectedEnvironment => EnvironmentComboBox.SelectedItem as string;

    private void DeployButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedEnvironment is null)
        {
            MessageBox.Show("Select an environment.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
