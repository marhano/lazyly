using System.Windows;

namespace PublishTool.Gui;

/// <summary>Generic "pick one of these environments" prompt -- used by the Projects tab's tests
/// dialog to ask which environment to run an E2E suite against on demand (a raw URL wouldn't make
/// sense there the way it does on the Publish tab, since running from the build history isn't tied
/// to any one publish-in-progress -- picking a registered environment is the more natural ask).
/// Distinct from <see cref="EnvironmentPickerDialog"/>, which is deploy-specific (splits by Local/
/// Remote target, labelled "Deploy") -- this is a single flat list with a caller-supplied message.</summary>
public partial class SelectEnvironmentDialog : Wpf.Ui.Controls.FluentWindow
{
    public SelectEnvironmentDialog(string message, IReadOnlyList<string> environmentNames, string? defaultEnvironmentName = null)
    {
        InitializeComponent();
        MessageTextBlock.Text = message;
        EnvironmentComboBox.ItemsSource = environmentNames;
        EnvironmentComboBox.SelectedItem = defaultEnvironmentName is not null && environmentNames.Contains(defaultEnvironmentName)
            ? defaultEnvironmentName
            : environmentNames.FirstOrDefault();
    }

    public string? SelectedEnvironment => EnvironmentComboBox.SelectedItem as string;

    private void RunButton_Click(object sender, RoutedEventArgs e)
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
