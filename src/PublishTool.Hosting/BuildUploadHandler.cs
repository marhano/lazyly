using System.Text.Json;
using PublishTool.Core.Models;
using PublishTool.Core.Services;

namespace PublishTool.Hosting;

/// <summary>Everything needed to accept an already-built zip + manifest.json (+ optional release
/// notes) -- shared by the human "Upload build files" form (<c>UploadBuildModel</c>) and the
/// <c>POST /api/builds/upload</c> endpoint, so the validate/write logic exists exactly once.
/// Takes <see cref="Stream"/>s rather than <c>IFormFile</c> so it isn't tied to Razor Pages model
/// binding.</summary>
/// <param name="MarkAsLatest">
/// Explicit true/false always overrides the uploaded manifest's own <c>IsLatest</c> -- used by the
/// human form, where "mark as latest" is a decision made at upload time regardless of what's baked
/// into a possibly-old local manifest. Null trusts the manifest's own value instead -- used by the
/// API endpoint, since a build coming from <c>Publisher</c> already has the right value computed
/// (the same "mark as latest" checkbox that produced the local manifest).
/// </param>
internal sealed record BuildUploadRequest(
    Stream ZipStream,
    string ZipFileName,
    Stream ManifestStream,
    Stream? ReleaseNotesStream,
    string? ReleaseNotesFileName,
    bool? MarkAsLatest);

internal sealed record BuildUploadResult(bool Success, string? ErrorMessage, string? ProjectName, string? Version, string? ManifestPath)
{
    public static BuildUploadResult Fail(string errorMessage) => new(false, errorMessage, null, null, null);

    public static BuildUploadResult Ok(string projectName, string version, string manifestPath) =>
        new(true, null, projectName, version, manifestPath);
}

internal sealed class BuildUploadHandler
{
    private readonly BuildRepository _buildRepository = new();

    private static readonly string[] AllowedArtifactExtensions = [".zip", ".apk", ".aab"];

    public async Task<BuildUploadResult> HandleAsync(string buildsRoot, BuildUploadRequest request, CancellationToken ct)
    {
        var artifactExtension = Path.GetExtension(request.ZipFileName);
        var isZip = artifactExtension.Equals(".zip", StringComparison.OrdinalIgnoreCase);
        if (!AllowedArtifactExtensions.Contains(artifactExtension, StringComparer.OrdinalIgnoreCase))
        {
            return BuildUploadResult.Fail("The build file must be a .zip, .apk, or .aab.");
        }

        if (request.ReleaseNotesFileName is not null &&
            !Path.GetExtension(request.ReleaseNotesFileName).Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            return BuildUploadResult.Fail("Release notes must be a .txt file.");
        }

        BuildManifest? manifest;
        try
        {
            manifest = await JsonSerializer.DeserializeAsync<BuildManifest>(request.ManifestStream, cancellationToken: ct);
        }
        catch (JsonException)
        {
            manifest = null;
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.ProjectName) || string.IsNullOrWhiteSpace(manifest.Version))
        {
            return BuildUploadResult.Fail(
                "That doesn't look like a valid manifest.json -- it must be the one PublishTool wrote alongside this build's zip.");
        }

        var projectName = manifest.ProjectName.Trim();
        var version = manifest.Version.Trim();

        if (!UploadValidation.IsValidPathSegment(projectName) || !UploadValidation.IsValidPathSegment(version))
        {
            return BuildUploadResult.Fail("The manifest's project name or version contains characters that aren't allowed in a file path.");
        }

        if (!SafeBuildPath.IsValidProjectName(buildsRoot, projectName))
        {
            return BuildUploadResult.Fail("Invalid project name.");
        }

        // Same version uploaded again overwrites in place instead of creating a duplicate --
        // matches how a republish of the same version already behaves.
        var paths = _buildRepository.ResolvePaths(buildsRoot, projectName, version, artifactExtension);

        await using (var fileStream = File.Create(paths.ZipPath))
        {
            await request.ZipStream.CopyToAsync(fileStream, ct);
        }

        // .apk/.aab have their own binary format guarantees -- this structural check only applies
        // to an actual .zip upload, the same "don't inspect the artifact's contents" stance the rest
        // of this pipeline already takes toward whatever's inside a build's zip.
        if (isZip && !UploadValidation.IsValidZip(paths.ZipPath))
        {
            File.Delete(paths.ZipPath);
            return BuildUploadResult.Fail("The uploaded build file isn't a valid .zip archive.");
        }

        string? releaseNotesPath = null;
        if (request.ReleaseNotesStream is not null)
        {
            await using var notesStream = File.Create(paths.ReleaseNotesPath);
            await request.ReleaseNotesStream.CopyToAsync(notesStream, ct);
            releaseNotesPath = paths.ReleaseNotesPath;
        }

        var isLatest = request.MarkAsLatest ?? manifest.IsLatest;

        // ZipPath/ReleaseNotesPath in the uploaded manifest point at wherever it was published
        // from and mean nothing here -- everything else (who/when/whether it's listed/app config)
        // comes straight from the manifest instead of asking the caller to resupply it.
        _buildRepository.WriteManifest(paths.ManifestPath, new BuildManifest
        {
            ProjectName = projectName,
            Version = version,
            PublishedAtUtc = manifest.PublishedAtUtc,
            PublishedBy = manifest.PublishedBy,
            ZipPath = paths.ZipPath,
            ListInHosting = manifest.ListInHosting,
            ReleaseNotesPath = releaseNotesPath,
            AppConfigSettings = manifest.AppConfigSettings,
            IsLatest = isLatest,
        });

        if (isLatest)
        {
            _buildRepository.SetLatest(buildsRoot, projectName, paths.ManifestPath);
        }

        return BuildUploadResult.Ok(projectName, version, paths.ManifestPath);
    }
}
