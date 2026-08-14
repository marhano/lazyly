using System.Text.Json;
using PublishTool.Core.Models;

namespace PublishTool.Hosting;

/// <summary>
/// The server-side half of the shared deployment environment list -- one JSON file directly under
/// BuildsRoot, mirroring how <see cref="SharedProjectStore"/> stores one file per project. Low
/// traffic (edited from the Settings tab, read whenever a project's environment dropdowns populate),
/// so no locking beyond what plain file I/O already gives.
/// </summary>
internal sealed class EnvironmentStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string FilePath(string buildsRoot) => Path.Combine(buildsRoot, "_environments.json");

    public EnvironmentSettings Get(string buildsRoot)
    {
        var path = FilePath(buildsRoot);
        if (!File.Exists(path))
        {
            return new EnvironmentSettings();
        }

        return JsonSerializer.Deserialize<EnvironmentSettings>(File.ReadAllText(path)) ?? new EnvironmentSettings();
    }

    public void Save(string buildsRoot, EnvironmentSettings settings)
    {
        File.WriteAllText(FilePath(buildsRoot), JsonSerializer.Serialize(settings, JsonOptions));
    }
}
