using System.Linq;
using System.Text.Json;
using PublishTool.Core.Models;

namespace PublishTool.Core.Services;

/// <summary>
/// The current test-run status per BUILD -- one JSON file holding a whole dictionary keyed by
/// project name + version (like <c>RemoteProjectRegistry</c>'s own local-overrides file), not an
/// append log, since only the *latest* result per build matters for the build-history grid's Test
/// Result column. Local mode keeps this under this dev's own <see cref="DefaultRoot"/>; remote mode
/// keeps it server-side on the dev server (see <c>Program.cs</c>'s <c>/api/tests/status</c>), so
/// every teammate viewing a remote-mode build history sees the same status regardless of who last
/// ran the tests.
/// </summary>
public sealed class TestRunStatusStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const string FileName = "status.json";

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PublishTool", "TestStatus");

    // "::" as a separator is safe here -- project names and versions are both filesystem-path-like
    // strings elsewhere in this app, and neither is expected to contain it.
    private static string MakeKey(string projectName, string version) => $"{projectName}::{version}";

    public async Task<IReadOnlyList<TestRunStatus>> LoadAllAsync(string root, CancellationToken ct = default)
    {
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path))
        {
            return Array.Empty<TestRunStatus>();
        }

        var json = await File.ReadAllTextAsync(path, ct);
        var all = JsonSerializer.Deserialize<Dictionary<string, TestRunStatus>>(json, JsonOptions);
        return all?.Values.ToList() ?? new List<TestRunStatus>();
    }

    public async Task SetAsync(string root, TestRunStatus status, CancellationToken ct = default)
    {
        var path = Path.Combine(root, FileName);
        Dictionary<string, TestRunStatus> all;
        if (File.Exists(path))
        {
            var json = await File.ReadAllTextAsync(path, ct);
            all = JsonSerializer.Deserialize<Dictionary<string, TestRunStatus>>(json, JsonOptions)
                ?? new Dictionary<string, TestRunStatus>(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            all = new Dictionary<string, TestRunStatus>(StringComparer.OrdinalIgnoreCase);
        }

        all[MakeKey(status.ProjectName, status.Version)] = status;

        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(all, JsonOptions), ct);
    }

    /// <summary>Merges freshly-run suite result(s) into this build's existing status instead of
    /// replacing the whole thing -- running just "E2E" leaves "Unit Test"'s last-known entry (and its
    /// own <see cref="TestSuiteRunStatus.RunAtUtc"/>) exactly as it was. Overall
    /// <see cref="TestRunStatus.Passed"/> is recomputed from the FULL merged suite list (old entries
    /// included), matching the existing "one failing suite fails the whole build" rule even when only
    /// one suite was actually re-run this time. Used by both local (GUI) and remote (Hosting) runs, so
    /// per-suite results behave identically either way.</summary>
    public async Task<TestRunStatus> MergeSuiteResultsAsync(
        string root, string projectName, string version, IReadOnlyList<TestSuiteRunStatus> newSuiteResults, string performedBy, CancellationToken ct = default)
    {
        var all = await LoadAllAsync(root, ct);
        var existing = all.FirstOrDefault(s =>
            string.Equals(s.ProjectName, projectName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(s.Version, version, StringComparison.Ordinal));

        var mergedSuites = existing?.Suites.ToList() ?? new List<TestSuiteRunStatus>();
        foreach (var newSuite in newSuiteResults)
        {
            mergedSuites.RemoveAll(s => string.Equals(s.SuiteName, newSuite.SuiteName, StringComparison.Ordinal));
            mergedSuites.Add(newSuite);
        }

        var status = new TestRunStatus
        {
            ProjectName = projectName,
            Version = version,
            Passed = mergedSuites.Count > 0 && mergedSuites.All(s => s.Passed),
            RunAtUtc = DateTimeOffset.UtcNow,
            PerformedBy = performedBy,
            Suites = mergedSuites,
        };

        await SetAsync(root, status, ct);
        return status;
    }
}
