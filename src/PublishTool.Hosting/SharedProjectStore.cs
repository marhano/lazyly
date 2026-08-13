using System.Text.Json;
using PublishTool.Core.Models;

namespace PublishTool.Hosting;

/// <summary>
/// The server-side half of the shared project registry -- one JSON file per project under
/// <c>{BuildsRoot}\_projects\</c>, mirroring how <c>BuildRepository</c> stores one manifest per
/// build. Low-traffic internal tool, so a single process-wide lock around the read-modify-write in
/// <see cref="ReserveNextSequence"/> is enough to avoid two devs' publishes racing for the same
/// release-notes sequence number -- no need for anything heavier.
/// </summary>
internal sealed class SharedProjectStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Static, not per-instance: callers new-up a fresh SharedProjectStore per request (same pattern
    // as BuildRepository elsewhere in this app), so an instance-level lock would protect nothing.
    private static readonly object Lock = new();

    private static string ProjectsDir(string buildsRoot) => Path.Combine(buildsRoot, "_projects");

    private static string ProjectFilePath(string buildsRoot, string name) =>
        Path.Combine(ProjectsDir(buildsRoot), $"{name}.json");

    public IReadOnlyList<SharedProjectConfig> ListProjects(string buildsRoot)
    {
        var dir = ProjectsDir(buildsRoot);
        if (!Directory.Exists(dir))
        {
            return Array.Empty<SharedProjectConfig>();
        }

        var results = new List<SharedProjectConfig>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            var project = JsonSerializer.Deserialize<SharedProjectConfig>(File.ReadAllText(file));
            if (project is not null)
            {
                results.Add(project);
            }
        }

        return results.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public SharedProjectConfig? GetProject(string buildsRoot, string name)
    {
        var path = ProjectFilePath(buildsRoot, name);
        return File.Exists(path) ? JsonSerializer.Deserialize<SharedProjectConfig>(File.ReadAllText(path)) : null;
    }

    /// <summary>Creates or updates a project's shared config. Deliberately ignores whatever
    /// <see cref="SharedProjectConfig.LastReleaseNotesSequence"/> the caller sent -- that counter is
    /// only ever advanced by <see cref="ReserveNextSequence"/>, never overwritten wholesale, so a
    /// client saving a stale copy of a project can never accidentally roll it backward.</summary>
    public void Upsert(string buildsRoot, SharedProjectConfig project)
    {
        lock (Lock)
        {
            var existing = GetProject(buildsRoot, project.Name);
            project.LastReleaseNotesSequence = existing?.LastReleaseNotesSequence ?? 0;
            WriteProject(buildsRoot, project);
        }
    }

    public bool Delete(string buildsRoot, string name)
    {
        var path = ProjectFilePath(buildsRoot, name);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    public int ReserveNextSequence(string buildsRoot, string name)
    {
        lock (Lock)
        {
            var project = GetProject(buildsRoot, name)
                ?? throw new InvalidOperationException($"Project '{name}' is not registered on this server.");

            var sequence = project.LastReleaseNotesSequence + 1;
            project.LastReleaseNotesSequence = sequence;
            WriteProject(buildsRoot, project);
            return sequence;
        }
    }

    private static void WriteProject(string buildsRoot, SharedProjectConfig project)
    {
        Directory.CreateDirectory(ProjectsDir(buildsRoot));
        File.WriteAllText(ProjectFilePath(buildsRoot, project.Name), JsonSerializer.Serialize(project, JsonOptions));
    }
}
