using System.IO.Compression;
using System.Text.Json;
using PublishTool.Core.Models;

namespace PublishTool.Core.Services;

public sealed record BuildArchiveResult(string ZipPath, string ManifestPath, string ReleaseNotesPath);

public sealed class BuildRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Zips <paramref name="sourceDir"/> straight into the build repository as
    /// "{ProjectName}/{Version}_{timestamp}.zip" -- one file per build, easy to list and download.
    /// </summary>
    public BuildArchiveResult Archive(string buildsRoot, string projectName, string version, string sourceDir)
    {
        var paths = ReservePaths(buildsRoot, projectName, version);

        // sourceDir is zipped as-is -- release notes are written separately (see WriteReleaseNotes),
        // never into sourceDir, so they never end up inside the deployed/zipped package.
        ZipFile.CreateFromDirectory(sourceDir, paths.ZipPath, CompressionLevel.Optimal, includeBaseDirectory: false);

        return paths;
    }

    /// <summary>
    /// Reserves the on-disk paths for a new build (creating the project folder if needed) without
    /// writing anything yet -- used when the zip itself comes from somewhere other than
    /// <see cref="Archive"/>'s directory-zipping, e.g. a browser upload streamed straight to disk.
    /// </summary>
    public BuildArchiveResult ReservePaths(string buildsRoot, string projectName, string version)
    {
        var projectDir = Path.Combine(buildsRoot, projectName);
        Directory.CreateDirectory(projectDir);

        var baseName = $"{version}_{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
        var zipPath = Path.Combine(projectDir, $"{baseName}.zip");
        var manifestPath = Path.Combine(projectDir, $"{baseName}.manifest.json");
        var releaseNotesPath = Path.Combine(projectDir, $"{baseName}.releasenotes.txt");

        return new BuildArchiveResult(zipPath, manifestPath, releaseNotesPath);
    }

    public void WriteManifest(string manifestPath, BuildManifest manifest)
    {
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
    }

    public void WriteReleaseNotes(string releaseNotesPath, string content)
    {
        File.WriteAllText(releaseNotesPath, content);
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

    /// <summary>
    /// Existing project folder names under the build repository -- used to suggest names when
    /// uploading a build, so uploads land in the same project folder as automated publishes
    /// instead of accidentally creating a near-duplicate (e.g. "OmniPay Business" vs "Omnipay Business").
    /// </summary>
    public IReadOnlyList<string> ListProjectNames(string buildsRoot)
    {
        if (!Directory.Exists(buildsRoot))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateDirectories(buildsRoot)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }
}
