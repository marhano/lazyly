using System.Text.Json;
using PublishTool.Core.Models;

namespace PublishTool.Core.Services;

/// <summary>
/// The "Remote" half of <see cref="IProjectRegistry"/> -- merges team-wide shared project config
/// (fetched from the Remote Build Hosting API) with this one dev's own local overrides (a small
/// file that never leaves this machine) into the full <see cref="ProjectConfig"/> every other part
/// of the app already knows how to use. See the field split in <see cref="SharedProjectConfig"/>/
/// <see cref="LocalProjectOverrides"/>, and <see cref="ProjectRegistryFactory"/> for how this gets
/// chosen over the plain local <see cref="ProjectRegistry"/>.
/// </summary>
public sealed class RemoteProjectRegistry : IProjectRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _baseUrl;
    private readonly string? _apiKey;
    private readonly string _localOverridesPath;
    private readonly RemoteHostingClient _client = new();

    public RemoteProjectRegistry(string baseUrl, string? apiKey, string localOverridesPath)
    {
        _baseUrl = baseUrl;
        _apiKey = apiKey;
        _localOverridesPath = localOverridesPath;
    }

    public static string DefaultLocalOverridesPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PublishTool",
        "project-overrides.json");

    public async Task<IReadOnlyList<ProjectConfig>> GetProjectsAsync(CancellationToken ct = default)
    {
        var shared = await _client.GetProjectsAsync(_baseUrl, _apiKey, ct);
        var overrides = LoadLocalOverrides();
        return shared.Select(s => Merge(s, overrides.GetValueOrDefault(s.Name))).ToList();
    }

    public async Task<ProjectConfig?> GetAsync(string name, CancellationToken ct = default)
    {
        var shared = await _client.GetProjectAsync(_baseUrl, _apiKey, name, ct);
        if (shared is null)
        {
            return null;
        }

        var overrides = LoadLocalOverrides();
        return Merge(shared, overrides.GetValueOrDefault(name));
    }

    public async Task AddOrUpdateAsync(ProjectConfig config, CancellationToken ct = default)
    {
        await _client.UpsertProjectAsync(_baseUrl, _apiKey, SharedProjectConfig.FromProjectConfig(config), ct);

        var overrides = LoadLocalOverrides();
        overrides[config.Name] = ToLocalOverrides(config);
        SaveLocalOverrides(overrides);
    }

    public async Task<bool> RemoveAsync(string name, CancellationToken ct = default)
    {
        await _client.DeleteProjectAsync(_baseUrl, _apiKey, name, ct);

        var overrides = LoadLocalOverrides();
        var removed = overrides.Remove(name);
        if (removed)
        {
            SaveLocalOverrides(overrides);
        }

        // The shared delete already succeeded (DeleteProjectAsync throws otherwise) -- report
        // success regardless of whether a local override happened to exist to remove too.
        return true;
    }

    public Task<int> ReserveNextReleaseSequenceAsync(string projectName, CancellationToken ct = default) =>
        _client.ReserveReleaseSequenceAsync(_baseUrl, _apiKey, projectName, ct);

    private Dictionary<string, LocalProjectOverrides> LoadLocalOverrides()
    {
        if (!File.Exists(_localOverridesPath))
        {
            return new Dictionary<string, LocalProjectOverrides>(StringComparer.OrdinalIgnoreCase);
        }

        var json = File.ReadAllText(_localOverridesPath);
        return JsonSerializer.Deserialize<Dictionary<string, LocalProjectOverrides>>(json)
            ?? new Dictionary<string, LocalProjectOverrides>(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveLocalOverrides(Dictionary<string, LocalProjectOverrides> overrides)
    {
        var dir = Path.GetDirectoryName(_localOverridesPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(_localOverridesPath, JsonSerializer.Serialize(overrides, JsonOptions));
    }

    private static ProjectConfig Merge(SharedProjectConfig shared, LocalProjectOverrides? local)
    {
        local ??= new LocalProjectOverrides();
        return new ProjectConfig
        {
            Name = shared.Name,
            ProjectId = shared.ProjectId,
            LastReleaseNotesSequence = shared.LastReleaseNotesSequence,
            CsprojPath = local.CsprojPath,
            PubxmlName = shared.PubxmlName,
            AssemblyInfoPath = local.AssemblyInfoPath,
            ExtraPublishTargets = shared.ExtraPublishTargets,
            LocalIisEnabled = local.LocalIisEnabled,
            LocalEnvironments = local.LocalEnvironments,
            SdkStyleProject = shared.SdkStyleProject,
            ListInHosting = shared.ListInHosting,
            UseAppConfig = shared.UseAppConfig,
            AppConfigType = shared.AppConfigType,
            AppConfigPath = local.AppConfigPath,
            UseEventLog = shared.UseEventLog,
            EventLogName = shared.EventLogName,
            EventLogFilterType = shared.EventLogFilterType,
            EventLogFilterValue = shared.EventLogFilterValue,
            EventLogMachineName = shared.EventLogMachineName,
            EventLogUsername = shared.EventLogUsername,
            EventLogProtectedPassword = local.EventLogProtectedPassword,
            RemoteIisEnabled = local.RemoteIisEnabled,
            RemoteEnvironments = shared.RemoteEnvironments,
        };
    }

    private static LocalProjectOverrides ToLocalOverrides(ProjectConfig config) => new()
    {
        CsprojPath = config.CsprojPath,
        AssemblyInfoPath = config.AssemblyInfoPath,
        LocalIisEnabled = config.LocalIisEnabled,
        LocalEnvironments = config.LocalEnvironments,
        RemoteIisEnabled = config.RemoteIisEnabled,
        AppConfigPath = config.AppConfigPath,
        EventLogProtectedPassword = config.EventLogProtectedPassword,
    };
}
