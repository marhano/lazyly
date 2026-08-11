using System.IO.Compression;
using System.Text.Json;
using PublishTool.Core.Models;

namespace PublishTool.Core.Services;

public sealed record BuildArchiveResult(string ZipPath, string ManifestPath);

public sealed class BuildRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Zips <paramref name="sourceDir"/> straight into the build repository as
    /// "{ProjectName}/{Version}_{timestamp}.zip" -- one file per build, easy to list and download.
    /// </summary>
    public BuildArchiveResult Archive(string buildsRoot, string projectName, string version, string sourceDir)
    {
        var projectDir = Path.Combine(buildsRoot, projectName);
        Directory.CreateDirectory(projectDir);

        var baseName = $"{version}_{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
        var zipPath = Path.Combine(projectDir, $"{baseName}.zip");
        var manifestPath = Path.Combine(projectDir, $"{baseName}.manifest.json");

        ZipFile.CreateFromDirectory(sourceDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);

        return new BuildArchiveResult(zipPath, manifestPath);
    }

    public void WriteManifest(string manifestPath, BuildManifest manifest)
    {
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
    }

    public IReadOnlyList<BuildManifest> ListBuilds(string buildsRoot, string? projectName = null)
    {
        if (!Directory.Exists(buildsRoot))
        {
            return Array.Empty<BuildManifest>();
        }

        var manifestFiles = Directory.EnumerateFiles(buildsRoot, "*.manifest.json", SearchOption.AllDirectories);
        var manifests = new List<BuildManifest>();

        foreach (var file in manifestFiles)
        {
            var manifest = JsonSerializer.Deserialize<BuildManifest>(File.ReadAllText(file));
            if (manifest is null)
            {
                continue;
            }

            if (projectName is not null && !string.Equals(manifest.ProjectName, projectName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            manifests.Add(manifest);
        }

        return manifests
            .OrderByDescending(m => m.PublishedAtUtc)
            .ToList();
    }
}
