using System.Text.Json;
using PublishTool.Core.Models;

namespace PublishTool.Core;

public sealed class ProjectRegistry
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

    public IReadOnlyList<ProjectConfig> Projects => _projects;

    public ProjectConfig? Get(string name) =>
        _projects.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public void AddOrUpdate(ProjectConfig config)
    {
        _projects.RemoveAll(p => string.Equals(p.Name, config.Name, StringComparison.OrdinalIgnoreCase));
        _projects.Add(config);
        Save();
    }

    public bool Remove(string name)
    {
        var removed = _projects.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            Save();
        }

        return removed;
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
