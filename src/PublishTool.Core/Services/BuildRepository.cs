using System.IO.Compression;
using System.Text.Json;
using PublishTool.Core.Models;

namespace PublishTool.Core.Services;

public sealed record BuildArchiveResult(string ZipPath, string ManifestPath, string ReleaseNotesPath);

public sealed record ExistingBuild(BuildManifest Manifest, string ManifestPath);

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
        WriteZip(paths.ZipPath, sourceDir);
        return paths;
    }

    /// <summary>
    /// Zips <paramref name="sourceDir"/> to an exact, already-known path -- used both by
    /// <see cref="Archive"/> and when overwriting an existing build in place (see
    /// <see cref="FindBuild"/>), where the destination must be the existing build's own zip path
    /// rather than a freshly reserved one. Overwrites if a file is already there.
    /// </summary>
    public void WriteZip(string zipPath, string sourceDir)
    {
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        // sourceDir is zipped as-is -- release notes are written separately (see WriteReleaseNotes),
        // never into sourceDir, so they never end up inside the deployed/zipped package.
        ZipFile.CreateFromDirectory(sourceDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
    }

    /// <summary>
    /// Finds the existing build (if any) for this exact project+version, so republishing the same
    /// version overwrites it in place instead of creating a duplicate. If more than one already
    /// exists (e.g. from before this behavior existed), the most recently published one wins --
    /// older duplicates are left on disk untouched rather than guessed at or deleted.
    /// </summary>
    public ExistingBuild? FindBuild(string buildsRoot, string projectName, string version)
    {
        var projectDir = Path.Combine(buildsRoot, projectName);
        if (!Directory.Exists(projectDir))
        {
            return null;
        }

        ExistingBuild? best = null;
        foreach (var file in Directory.EnumerateFiles(projectDir, "*.manifest.json"))
        {
            var manifest = JsonSerializer.Deserialize<BuildManifest>(File.ReadAllText(file));
            if (manifest is null || !string.Equals(manifest.Version, version, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (best is null || manifest.PublishedAtUtc > best.Manifest.PublishedAtUtc)
            {
                best = new ExistingBuild(manifest, file);
            }
        }

        return best;
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

    /// <summary>
    /// Resolves the paths an upload for this exact project+version should write to: an existing
    /// build's own paths if one is already there (so uploading the same version again overwrites
    /// it in place instead of creating a duplicate, matching how <see cref="Publisher"/> already
    /// treats a republish of the same version), or freshly reserved ones otherwise. This is the
    /// one place every upload entry point (the manual Upload form, the build-files upload, and the
    /// remote hosting API) decides "new build or overwrite?".
    /// </summary>
    public BuildArchiveResult ResolvePaths(string buildsRoot, string projectName, string version)
    {
        var existing = FindBuild(buildsRoot, projectName, version);
        if (existing is null)
        {
            return ReservePaths(buildsRoot, projectName, version);
        }

        // Pre-this-feature manifests may not have a release notes path yet -- fall back to the
        // naming convention derived from the existing zip, same as Publisher does.
        var releaseNotesPath = existing.Manifest.ReleaseNotesPath
            ?? Path.ChangeExtension(existing.Manifest.ZipPath, null) + ".releasenotes.txt";

        return new BuildArchiveResult(existing.Manifest.ZipPath, existing.ManifestPath, releaseNotesPath);
    }

    public void WriteManifest(string manifestPath, BuildManifest manifest)
    {
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
    }

    /// <summary>
    /// Flags the build at <paramref name="latestManifestPath"/> as this project's "latest release"
    /// and un-flags every other build for the same project -- the only way <see cref="BuildManifest.IsLatest"/>
    /// should ever be set to true, so at most one build per project can ever hold it. Only rewrites
    /// manifests whose flag actually changes, so re-marking the same build already-latest is a no-op.
    /// </summary>
    public void SetLatest(string buildsRoot, string projectName, string latestManifestPath)
    {
        var projectDir = Path.Combine(buildsRoot, projectName);
        if (!Directory.Exists(projectDir))
        {
            return;
        }

        var targetFullPath = Path.GetFullPath(latestManifestPath);

        foreach (var file in Directory.EnumerateFiles(projectDir, "*.manifest.json"))
        {
            var manifest = JsonSerializer.Deserialize<BuildManifest>(File.ReadAllText(file));
            if (manifest is null)
            {
                continue;
            }

            var shouldBeLatest = string.Equals(Path.GetFullPath(file), targetFullPath, StringComparison.OrdinalIgnoreCase);
            if (manifest.IsLatest != shouldBeLatest)
            {
                manifest.IsLatest = shouldBeLatest;
                WriteManifest(file, manifest);
            }
        }
    }

    public void WriteReleaseNotes(string releaseNotesPath, string content)
    {
        File.WriteAllText(releaseNotesPath, content);
    }

    /// <summary>Deletes a build entirely: its zip, its release notes (if any), and the manifest
    /// itself. Identified by the manifest's own path rather than project+version, since more than
    /// one manifest can exist for the same version (see <see cref="FindBuild"/>) -- this always
    /// removes exactly the one build the manifest path points to. A no-op if the manifest is
    /// already gone.</summary>
    public void DeleteBuild(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return;
        }

        var manifest = JsonSerializer.Deserialize<BuildManifest>(File.ReadAllText(manifestPath));
        if (manifest is not null)
        {
            if (File.Exists(manifest.ZipPath))
            {
                File.Delete(manifest.ZipPath);
            }

            if (manifest.ReleaseNotesPath is not null && File.Exists(manifest.ReleaseNotesPath))
            {
                File.Delete(manifest.ReleaseNotesPath);
            }
        }

        File.Delete(manifestPath);
    }

    /// <summary>Updates just <see cref="BuildManifest.ListInHosting"/> and/or
    /// <see cref="BuildManifest.IsLatest"/> on an existing build without touching its zip/release
    /// notes. Setting <paramref name="isLatest"/> to true delegates to <see cref="SetLatest"/> (which
    /// un-flags every other build for the project) rather than duplicating that logic here. Returns
    /// null if the manifest doesn't exist.</summary>
    public BuildManifest? UpdateMetadata(string buildsRoot, string projectName, string manifestPath, bool? listInHosting, bool? isLatest)
    {
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var manifest = JsonSerializer.Deserialize<BuildManifest>(File.ReadAllText(manifestPath));
        if (manifest is null)
        {
            return null;
        }

        if (listInHosting.HasValue)
        {
            manifest.ListInHosting = listInHosting.Value;
        }

        if (isLatest == false)
        {
            manifest.IsLatest = false;
        }

        WriteManifest(manifestPath, manifest);

        if (isLatest == true)
        {
            SetLatest(buildsRoot, projectName, manifestPath);
            manifest.IsLatest = true;
        }

        return manifest;
    }

    public IReadOnlyList<BuildManifest> ListBuilds(string buildsRoot, string? projectName = null) =>
        ListBuildsWithPaths(buildsRoot, projectName).Select(b => b.Manifest).ToList();

    /// <summary>Same listing as <see cref="ListBuilds"/>, but keeps each manifest's own file path
    /// alongside it -- needed by anything that has to act on one specific build afterward (delete,
    /// update, or an API response's <c>ManifestPath</c> token), since project+version alone isn't
    /// guaranteed unique (see <see cref="FindBuild"/>).</summary>
    public IReadOnlyList<(BuildManifest Manifest, string ManifestPath)> ListBuildsWithPaths(string buildsRoot, string? projectName = null)
    {
        if (!Directory.Exists(buildsRoot))
        {
            return Array.Empty<(BuildManifest, string)>();
        }

        var manifestFiles = Directory.EnumerateFiles(buildsRoot, "*.manifest.json", SearchOption.AllDirectories);
        var results = new List<(BuildManifest Manifest, string ManifestPath)>();

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

            results.Add((manifest, file));
        }

        return results
            .OrderByDescending(b => b.Manifest.PublishedAtUtc)
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
