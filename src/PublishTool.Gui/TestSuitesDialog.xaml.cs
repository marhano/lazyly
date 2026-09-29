using System.Collections.ObjectModel;
using System.Windows;
using PublishTool.Core.Models;

namespace PublishTool.Gui;

/// <summary>One row in <see cref="TestSuitesDialog"/>'s table -- one of the project's configured
/// test suites (see <see cref="ProjectConfig.TestSuites"/>), joined with its own last-known result
/// for the specific build this dialog was opened for (see <see cref="TestRunStatus"/>'s remarks on
/// why suites are tracked and merged independently).</summary>
public sealed class TestSuiteRow
{
    public required string SuiteName { get; init; }

    public TestSuiteRunStatus? Status { get; init; }

    public string ResultDisplay => Status switch
    {
        null => "Not run",
        { Passed: true } s => $"Passed ({s.PassedCount}/{s.TotalCount})",
        var s => $"Failed ({s.FailedCount}/{s.TotalCount})",
    };

    public string LastRunDisplay => Status is null ? string.Empty : $"{Status.RunAtUtc.ToLocalTime():g} by {Status.PerformedBy}";
}

/// <summary>Replaces the old build-history grid's inline "Test Result" column/"Run Tests" button --
/// opened per-build from the Projects tab, this shows every one of the project's configured test
/// suite types (Unit Test, E2E, ...) in their own table, each runnable and viewable independently
/// instead of one combined "Run Tests" action covering all of them at once. The caller (MainWindow)
/// resolves how a suite actually gets run and how status gets (re)loaded -- local execution vs. the
/// dev server, and where the resulting output goes -- this dialog only displays the table, prompts
/// for a target environment when running E2E (see <see cref="SelectEnvironmentDialog"/>), and
/// dispatches a suite name (+ chosen environment, if any) to run, same "caller resolves, dialog
/// displays" split as <see cref="DeploymentHistoryDialog"/>.</summary>
public partial class TestSuitesDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly string _projectName;
    private readonly string _version;
    private readonly IReadOnlyList<TestSuiteConfig> _suites;
    private readonly IReadOnlyList<string> _remoteEnvironmentNames;
    private readonly string? _defaultE2EEnvironmentName;
    private readonly Func<Task<IReadOnlyList<TestRunStatus>>> _loadStatusesAsync;
    private readonly Func<string, string?, Task> _runSuiteAsync;
    private readonly ObservableCollection<TestSuiteRow> _rows = new();

    public TestSuitesDialog(
        string projectName,
        string version,
        IReadOnlyList<TestSuiteConfig> suites,
        IReadOnlyList<string> remoteEnvironmentNames,
        string? defaultE2EEnvironmentName,
        Func<Task<IReadOnlyList<TestRunStatus>>> loadStatusesAsync,
        Func<string, string?, Task> runSuiteAsync)
    {
        InitializeComponent();
        _projectName = projectName;
        _version = version;
        _suites = suites;
        _remoteEnvironmentNames = remoteEnvironmentNames;
        _defaultE2EEnvironmentName = defaultE2EEnvironmentName;
        _loadStatusesAsync = loadStatusesAsync;
        _runSuiteAsync = runSuiteAsync;

        TitleTextBlock.Text = $"Tests: {projectName} v{version}";
        TestSuitesDataGrid.ItemsSource = _rows;

        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        IReadOnlyList<TestRunStatus> statuses;
        try
        {
            statuses = await _loadStatusesAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't load test status: {ex.Message}", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            statuses = Array.Empty<TestRunStatus>();
        }

        var status = statuses.FirstOrDefault(s =>
            string.Equals(s.ProjectName, _projectName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(s.Version, _version, StringComparison.Ordinal));

        _rows.Clear();
        foreach (var suite in _suites)
        {
            _rows.Add(new TestSuiteRow
            {
                SuiteName = suite.Name,
                Status = status?.Suites.FirstOrDefault(s => string.Equals(s.SuiteName, suite.Name, StringComparison.Ordinal)),
            });
        }
    }

    private async void RunSuiteButton_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not TestSuiteRow row)
        {
            return;
        }

        // E2E has no single "the" target the way Unit Test does -- ask which of the project's
        // environments to run against every time, rather than always using whatever's configured as
        // the project's default (see ProjectEditDialog's Shared settings).
        string? environmentName = null;
        if (string.Equals(row.SuiteName, TestSuiteTypeNames.DisplayName(TestSuiteType.E2E), StringComparison.Ordinal))
        {
            if (_remoteEnvironmentNames.Count == 0)
            {
                MessageBox.Show(
                    "This project has no dev-server environments configured to run E2E against -- add one in the project's Edit dialog first.",
                    "PublishTool", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var picker = new SelectEnvironmentDialog(
                $"Run '{row.SuiteName}' against which environment?", _remoteEnvironmentNames, _defaultE2EEnvironmentName)
            {
                Owner = this,
            };
            if (picker.ShowDialog() != true || picker.SelectedEnvironment is not { } chosenEnvironment)
            {
                return;
            }

            environmentName = chosenEnvironment;
        }

        TestSuitesDataGrid.IsEnabled = false;
        try
        {
            await _runSuiteAsync(row.SuiteName, environmentName);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't run '{row.SuiteName}': {ex.Message}", "PublishTool", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            TestSuitesDataGrid.IsEnabled = true;
        }

        await RefreshAsync();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
