using System.Collections.ObjectModel;
using System.Windows;
using PublishTool.Core.Models;

namespace PublishTool.Gui;

/// <summary>
/// Add or edit one <see cref="DeploymentEnvironment"/> (name, auto-create-site, and its site
/// bindings) in a single dialog, opened from either the Local or Remote environments section of
/// <see cref="ProjectEditDialog"/> -- same shape for both, since <see cref="DeploymentEnvironment"/>
/// itself doesn't distinguish local from remote. <see cref="HostRootPath"/> is deliberately not
/// editable here -- every environment on the same side shares one root, set once above the grid in
/// <see cref="ProjectEditDialog"/>, not per-row.
/// </summary>
public partial class EditEnvironmentDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly ObservableCollection<IisBinding> _bindings;
    private readonly string? _hostRootPath;

    public EditEnvironmentDialog(DeploymentEnvironment? existing, IReadOnlyList<string> environmentNames, string? defaultEnvironmentName)
    {
        InitializeComponent();
        Title = existing is null ? "Add environment" : $"Edit {existing.Name}";

        EnvironmentNameComboBox.ItemsSource = environmentNames;
        EnvironmentNameComboBox.Text = existing?.Name ?? defaultEnvironmentName ?? environmentNames.FirstOrDefault() ?? string.Empty;

        AutoCreateSiteToggle.IsChecked = existing?.AutoCreateSite ?? false;
        _hostRootPath = existing?.HostRootPath;

        _bindings = new ObservableCollection<IisBinding>(
            existing?.Bindings.Select(b => new IisBinding { Protocol = b.Protocol, IpAddress = b.IpAddress, Port = b.Port, HostName = b.HostName })
            ?? Enumerable.Empty<IisBinding>());
        BindingsDataGrid.ItemsSource = _bindings;
    }

    /// <summary>Only valid to read after <see cref="Window.ShowDialog"/> returns true.</summary>
    public DeploymentEnvironment Result => new()
    {
        Name = EnvironmentNameComboBox.Text.Trim(),
        HostRootPath = _hostRootPath,
        AutoCreateSite = AutoCreateSiteToggle.IsChecked == true,
        Bindings = _bindings.ToList(),
    };

    private void AddBindingButton_Click(object sender, RoutedEventArgs e) =>
        _bindings.Add(new IisBinding { Protocol = "http", IpAddress = "*", Port = 80, HostName = null });

    private void RemoveBindingButton_Click(object sender, RoutedEventArgs e)
    {
        if (BindingsDataGrid.SelectedItem is IisBinding binding)
        {
            _bindings.Remove(binding);
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // Flush any in-progress cell edit (typed a value, clicked Save directly without tabbing
        // away first) into the bound IisBinding before _bindings is read above.
        BindingsDataGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
        BindingsDataGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);

        if (string.IsNullOrWhiteSpace(EnvironmentNameComboBox.Text))
        {
            MessageBox.Show("Enter or pick an environment name.", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (AutoCreateSiteToggle.IsChecked == true && _bindings.Count == 0)
        {
            MessageBox.Show(
                "Auto-create site is on but no bindings were added. Add at least one, or turn it off.",
                "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
