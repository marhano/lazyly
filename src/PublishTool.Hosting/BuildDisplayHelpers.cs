using PublishTool.Core.Models;

namespace PublishTool.Hosting;

/// <summary>
/// Formatting/path helpers shared by every page that renders a builds table or list (the landing
/// page's "Recent builds" preview, <c>/Builds</c>, and <c>/Project/{name}</c>) -- kept static and
/// parameterized by <c>buildsRootPath</c> instead of living on one page's PageModel, so a Razor
/// partial shared across those pages can call them without needing an instance.
/// </summary>
internal static class BuildDisplayHelpers
{
    public static string RelativeZipPath(string buildsRootPath, BuildManifest build) =>
        Path.GetRelativePath(buildsRootPath, build.ZipPath);

    public static bool HasReleaseNotes(BuildManifest build) =>
        build.ReleaseNotesPath is not null && File.Exists(build.ReleaseNotesPath);

    public static string RelativeReleaseNotesPath(string buildsRootPath, BuildManifest build) =>
        Path.GetRelativePath(buildsRootPath, build.ReleaseNotesPath!);

    public static long GetFileSizeBytes(string zipPath) =>
        File.Exists(zipPath) ? new FileInfo(zipPath).Length : -1;

    public static string FormatFileSize(string zipPath)
    {
        var bytes = GetFileSizeBytes(zipPath);
        if (bytes < 0)
        {
            return "-";
        }

        double size = bytes;
        string[] units = { "B", "KB", "MB", "GB" };
        var unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.#} {units[unitIndex]}";
    }

    /// <summary>Release notes text keyed by each build's relative release-notes path (the same key
    /// used in the "View" button's data attribute and the /download?path= query string), for the
    /// modal to read client-side without a round trip per click.</summary>
    public static Dictionary<string, ReleaseNotesModalEntry> BuildReleaseNotesMap(string buildsRootPath, IEnumerable<BuildManifest> builds)
    {
        var releaseNotes = new Dictionary<string, ReleaseNotesModalEntry>();
        foreach (var build in builds)
        {
            if (!HasReleaseNotes(build))
            {
                continue;
            }

            var key = RelativeReleaseNotesPath(buildsRootPath, build);
            releaseNotes[key] = new ReleaseNotesModalEntry($"{build.ProjectName} v{build.Version}", File.ReadAllText(build.ReleaseNotesPath!));
        }

        return releaseNotes;
    }
}

public sealed record ReleaseNotesModalEntry(string Title, string Text);

/// <summary>One project's summary for the landing page's project cards -- build count and the most
/// recently published build, regardless of whether that build happens to be flagged "latest release"
/// (see <see cref="BuildManifest.IsLatest"/>, a separate, deliberately-pinned concept).</summary>
public sealed record ProjectSummary(string Name, int BuildCount, BuildManifest? MostRecentBuild);

/// <summary>Shared model for <c>Pages/Shared/_BuildsTable.cshtml</c>, the builds table+filter+release-notes-modal
/// markup reused by <c>/Builds</c> (every project, filterable) and <c>/Project/{name}</c> (one project,
/// filter hidden since it'd be redundant).</summary>
public sealed class BuildsTableViewModel
{
    public required IReadOnlyList<BuildManifest> Builds { get; init; }

    public required string BuildsRootPath { get; init; }

    /// <summary>Null or empty hides the project filter dropdown entirely.</summary>
    public IReadOnlyList<string> ProjectNames { get; init; } = Array.Empty<string>();

    public required IReadOnlyDictionary<string, ReleaseNotesModalEntry> ReleaseNotesByPath { get; init; }
}
