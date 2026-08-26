using System.Text.Json;
using System.Text.Json.Nodes;
using PublishTool.Core.Models;

namespace PublishTool.Core.Services.AppConfig;

/// <summary>Reads/writes flat, top-level scalar values in an ASP.NET Core-style appsettings.json.
/// Nested sections (ConnectionStrings, Logging, etc.) are left completely untouched on both read
/// (only top-level scalars are exposed as settings) and write (only the given keys are
/// updated/added; everything else in the file is preserved).</summary>
public sealed class AppSettingsJsonProvider : IAppConfigProvider
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public string TypeName => "AppSettingsJson";

    public string DisplayName => "appsettings.json";

    public IReadOnlyList<ProjectType> ApplicableProjectTypes => [ProjectType.DotNet];

    public Dictionary<string, string> ReadSettings(string configPath)
    {
        var root = LoadRoot(configPath);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in root)
        {
            if (value is JsonValue scalar)
            {
                result[key] = scalar.TryGetValue<string>(out var s) ? s : scalar.ToJsonString();
            }
        }

        return result;
    }

    public void WriteSettings(string configPath, IReadOnlyDictionary<string, string> settings)
    {
        var root = LoadRoot(configPath);

        foreach (var (key, value) in settings)
        {
            // Round-trips bools/numbers as their proper JSON type where the text unambiguously
            // parses back to one, instead of silently turning e.g. a numeric setting into a JSON
            // string every time this writes the file, even for keys nothing actually changed.
            root[key] = bool.TryParse(value, out var boolValue) ? JsonValue.Create(boolValue)
                : long.TryParse(value, out var longValue) ? JsonValue.Create(longValue)
                : double.TryParse(value, out var doubleValue) ? JsonValue.Create(doubleValue)
                : JsonValue.Create(value);
        }

        File.WriteAllText(configPath, root.ToJsonString(WriteOptions));
    }

    public IReadOnlyList<string> FindCandidateConfigPaths(string sourceRoot) =>
        ConfigFileSearch.FindFiles(sourceRoot, name => string.Equals(name, "appsettings.json", StringComparison.OrdinalIgnoreCase));

    private static JsonObject LoadRoot(string configPath) =>
        JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject
            ?? throw new InvalidOperationException($"'{configPath}' is not a JSON object.");
}
