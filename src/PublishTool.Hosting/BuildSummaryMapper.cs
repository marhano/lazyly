using PublishTool.Core.Models;

namespace PublishTool.Hosting;

/// <summary>Maps a <see cref="BuildManifest"/> (whose Zip/ReleaseNotes paths are absolute, local
/// to whichever machine wrote them) to the API's <see cref="BuildSummaryDto"/> (whose paths are
/// relative to BuildsRoot) -- used by every <c>/api/builds</c> response so the server's own
/// absolute paths are never exposed to a remote client.</summary>
internal static class BuildSummaryMapper
{
    public static BuildSummaryDto ToDto(string buildsRoot, BuildManifest manifest, string manifestPath) => new()
    {
        ProjectName = manifest.ProjectName,
        Version = manifest.Version,
        PublishedAtUtc = manifest.PublishedAtUtc,
        PublishedBy = manifest.PublishedBy,
        ListInHosting = manifest.ListInHosting,
        IsLatest = manifest.IsLatest,
        ManifestPath = SafeBuildPath.ToRelative(buildsRoot, manifestPath),
        ZipPath = SafeBuildPath.ToRelative(buildsRoot, manifest.ZipPath),
        ReleaseNotesPath = manifest.ReleaseNotesPath is null ? null : SafeBuildPath.ToRelative(buildsRoot, manifest.ReleaseNotesPath),
    };
}
