using System.Text.Json;
using PublishTool.Core.Models;

namespace PublishTool.Core.Services;

public sealed class LocalEnvironmentRegistry : IEnvironmentRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;

    public LocalEnvironmentRegistry(string path)
    {
        _path = path;
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PublishTool",
        "environments.json");

    public Task<EnvironmentSettings> GetAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
        {
            return Task.FromResult(new EnvironmentSettings());
        }

        var json = File.ReadAllText(_path);
        return Task.FromResult(JsonSerializer.Deserialize<EnvironmentSettings>(json) ?? new EnvironmentSettings());
    }

    public Task SaveAsync(EnvironmentSettings settings, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions));
        return Task.CompletedTask;
    }
}
