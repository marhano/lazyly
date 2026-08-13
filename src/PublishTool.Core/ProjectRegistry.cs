using System.Text.Json;
using PublishTool.Core.Models;

namespace PublishTool.Core;

/// <summary>Local, single-file project registry -- the "Local" half of <see cref="IProjectRegistry"/>.
/// See <see cref="Services.ProjectRegistryFactory"/> for how this and
/// <see cref="Services.RemoteProjectRegistry"/> get chosen.</summary>
public sealed class ProjectRegistry : IProjectRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _registryPath;
    private List<ProjectConfig> _projects = new();

    public ProjectRegistry(string registryPath)
    {
        _registryPath = registryPath;
        Load();
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PublishTool",
        "projects.json");

    public Task<IReadOnlyList<ProjectConfig>> GetProjectsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProjectConfig>>(_projects);

    public Task<ProjectConfig?> GetAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(_projects.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)));

    public Task AddOrUpdateAsync(ProjectConfig config, CancellationToken ct = default)
    {
        _projects.RemoveAll(p => string.Equals(p.Name, config.Name, StringComparison.OrdinalIgnoreCase));
        _projects.Add(config);
        Save();
        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(string name, CancellationToken ct = default)
    {
        var removed = _projects.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            Save();
        }

        return Task.FromResult(removed);
    }

    public Task<int> ReserveNextReleaseSequenceAsync(string projectName, CancellationToken ct = default)
    {
        var project = _projects.FirstOrDefault(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Project '{projectName}' is not registered.");

        var sequence = project.LastReleaseNotesSequence + 1;
        project.LastReleaseNotesSequence = sequence;
        Save();
        return Task.FromResult(sequence);
    }

    private void Load()
    {
        if (!File.Exists(_registryPath))
        {
            return;
        }

        var json = File.ReadAllText(_registryPath);
        _projects = JsonSerializer.Deserialize<List<ProjectConfig>>(json) ?? new List<ProjectConfig>();
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_registryPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(_projects, JsonOptions);
        File.WriteAllText(_registryPath, json);
    }
}
