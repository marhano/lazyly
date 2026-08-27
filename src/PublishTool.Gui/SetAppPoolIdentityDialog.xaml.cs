using System.Windows;
using PublishTool.Core.Models;

namespace PublishTool.Gui;

/// <summary>Lets the user pick one of the allow-listed <see cref="AppPoolIdentityType"/> values for
/// a single application pool -- see that enum for why it's a fixed list, not a free-text account.</summary>
public partial class SetAppPoolIdentityDialog : Wpf.Ui.Controls.FluentWindow
{
    private sealed record IdentityOption(AppPoolIdentityType Value, string DisplayName);

    public AppPoolIdentityType SelectedIdentityType { get; private set; }

    public SetAppPoolIdentityDialog(string poolName)
    {
        InitializeComponent();
        PoolNameTextBlock.Text = $"Application pool: {poolName}";

        var options = new[]
        {
            new IdentityOption(AppPoolIdentityType.ApplicationPoolIdentity, "ApplicationPoolIdentity (default, least privileged)"),
            new IdentityOption(AppPoolIdentityType.NetworkService, "NetworkService"),
            new IdentityOption(AppPoolIdentityType.LocalService, "LocalService"),
            new IdentityOption(AppPoolIdentityType.LocalSystem, "LocalSystem (most privileged)"),
        };
        IdentityTypeComboBox.ItemsSource = options;
        IdentityTypeComboBox.SelectedIndex = 0;
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (IdentityTypeComboBox.SelectedItem is not IdentityOption selected)
        {
            return;
        }

        SelectedIdentityType = selected.Value;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
