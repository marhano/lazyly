using System.Windows;
using System.Windows.Controls;
using PublishTool.Core.Services;

namespace PublishTool.Gui;

/// <summary>Edits an existing Firewall tab rule in place (same underlying netsh rule identity,
/// not remove-and-recreate) -- opened from the Firewall tab's "Edit selected rule" button, same
/// small-dialog pattern as <see cref="EnvironmentPickerDialog"/>.</summary>
public partial class EditFirewallRuleDialog : Wpf.Ui.Controls.FluentWindow
{
    public EditFirewallRuleDialog(string label, string ports, string protocol)
    {
        InitializeComponent();
        LabelTextBox.Text = label;
        PortsTextBox.Text = ports;

        foreach (ComboBoxItem item in ProtocolComboBox.Items)
        {
            if (string.Equals(item.Content as string, protocol, StringComparison.OrdinalIgnoreCase))
            {
                ProtocolComboBox.SelectedItem = item;
                break;
            }
        }

        ProtocolComboBox.SelectedItem ??= ProtocolComboBox.Items[0];
    }

    public string Label => LabelTextBox.Text.Trim();

    public string Ports => PortsTextBox.Text.Trim();

    public string Protocol => (ProtocolComboBox.SelectedItem as ComboBoxItem)?.Content as string ?? "TCP";

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Label))
        {
            MessageBox.Show("Enter a label.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            FirewallManager.ValidatePortSpec(Ports);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
