using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using PublishTool.Commands;
using PublishTool.Core;
using PublishTool.Core.Models;

namespace PublishTool.Gui;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly GuiOutputSink _output;
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private bool _isBusy;

    public MainWindow()
    {
        InitializeComponent();

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "PublishTool",
            Visible = true,
        };
        Closed += (_, _) => _notifyIcon.Dispose();

        _output = new GuiOutputSink(OutputLogBox, StatusTextBlock, _notifyIcon);
        RefreshProjects();
        LoadSettingsIntoForm();
    }

    private void LoadSettingsIntoForm()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        BuildsRootTextBox.Text = settings.BuildsRoot;
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

        var args = new[]
        {
            "publish",
            "--project", project,
            "--version", VersionTextBox.Text,
        };

        await RunAsync(args);
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

        var args = new List<string>
        {
            "add-project",
            "--name", NewProjectNameTextBox.Text,
            "--csproj", NewProjectCsprojTextBox.Text,
            "--pubxml", NewProjectPubxmlTextBox.Text,
            "--iis-host", NewProjectIisHostTextBox.Text,
        };

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
        NewProjectCsprojTextBox.Clear();
        NewProjectPubxmlTextBox.Clear();
        NewProjectAssemblyInfoTextBox.Clear();
        NewProjectIisHostTextBox.Clear();
        NewProjectExtraTargetsTextBox.Clear();
        RegisteredProjectsListBox.SelectedItem = null;
    }

    private void RegisteredProjectsListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (RegisteredProjectsListBox.SelectedItem is not ProjectConfig project)
        {
            return;
        }

        NewProjectNameTextBox.Text = project.Name;
        NewProjectCsprojTextBox.Text = project.CsprojPath;
        NewProjectPubxmlTextBox.Text = project.PubxmlName;
        NewProjectAssemblyInfoTextBox.Text = project.AssemblyInfoPath ?? string.Empty;
        NewProjectIisHostTextBox.Text = project.IisHostPath;
        NewProjectExtraTargetsTextBox.Text = project.ExtraPublishTargets ?? string.Empty;
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
    }
}
