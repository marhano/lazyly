using Microsoft.AspNetCore.Mvc.RazorPages;
using PublishTool.Core.Models;
using PublishTool.Core.Services;

namespace PublishTool.Hosting.Pages;

public class IndexModel : PageModel
{
    private readonly IConfiguration _configuration;
    private readonly BuildRepository _buildRepository = new();

    public IndexModel(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>BuildsRoot has no value in configuration at all.</summary>
    public bool BuildsRootNotConfigured { get; private set; }

    /// <summary>BuildsRoot has a value, but the folder isn't there or this process can't see it
    /// (very often an IIS app pool identity permission issue, not a missing folder).</summary>
    public bool BuildsRootNotAccessible { get; private set; }

    public string BuildsRootPath { get; private set; } = string.Empty;

    public string RunningAs { get; private set; } = Environment.UserName;

    public IReadOnlyList<BuildManifest> Builds { get; private set; } = Array.Empty<BuildManifest>();

    /// <summary>Distinct project names among <see cref="Builds"/>, for the project filter dropdown.</summary>
    public IReadOnlyList<string> ProjectNames { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Release notes text keyed by each build's relative release-notes path (the same key used
    /// in the "View" button's data attribute and the /download?path= query string), serialized
    /// into the page for the modal to read client-side -- avoids a round trip per click.
    /// </summary>
    public IReadOnlyDictionary<string, ReleaseNotesModalEntry> ReleaseNotesByPath { get; private set; } =
        new Dictionary<string, ReleaseNotesModalEntry>();

    public void OnGet()
    {
        BuildsRootPath = _configuration["BuildsRoot"] ?? string.Empty;

        if (string.IsNullOrWhiteSpace(BuildsRootPath))
        {
            BuildsRootNotConfigured = true;
            return;
        }

        if (!Directory.Exists(BuildsRootPath))
        {
            BuildsRootNotAccessible = true;
            return;
        }

        Builds = _buildRepository.ListBuilds(BuildsRootPath)
            .Where(b => b.ListInHosting)
            .OrderByDescending(b => b.PublishedAtUtc)
            .ToList();

        ProjectNames = Builds
            .Select(b => b.ProjectName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var releaseNotes = new Dictionary<string, ReleaseNotesModalEntry>();
        foreach (var build in Builds)
        {
            if (build.ReleaseNotesPath is null || !System.IO.File.Exists(build.ReleaseNotesPath))
            {
                continue;
            }

            var key = RelativeReleaseNotesPath(build);
            releaseNotes[key] = new ReleaseNotesModalEntry(
                $"{build.ProjectName} v{build.Version}",
                System.IO.File.ReadAllText(build.ReleaseNotesPath));
        }

        ReleaseNotesByPath = releaseNotes;
    }

    public string RelativeZipPath(BuildManifest build) => Path.GetRelativePath(BuildsRootPath, build.ZipPath);

    public bool HasReleaseNotes(BuildManifest build) =>
        build.ReleaseNotesPath is not null && System.IO.File.Exists(build.ReleaseNotesPath);

    public string RelativeReleaseNotesPath(BuildManifest build) =>
        Path.GetRelativePath(BuildsRootPath, build.ReleaseNotesPath!);

    public static long GetFileSizeBytes(string zipPath)
    {
        // Fully qualified: PageModel has its own File(...) method, which shadows System.IO.File.
        return System.IO.File.Exists(zipPath) ? new FileInfo(zipPath).Length : -1;
    }

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
}

public sealed record ReleaseNotesModalEntry(string Title, string Text);
