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
}
