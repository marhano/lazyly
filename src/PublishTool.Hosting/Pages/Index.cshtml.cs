using Microsoft.AspNetCore.Mvc.RazorPages;
using PublishTool.Core.Models;
using PublishTool.Core.Services;

namespace PublishTool.Hosting.Pages;

/// <summary>
/// The landing page: a card per project (click through to <see cref="ProjectModel"/> for that
/// project's full build list) and a short "Recent builds" preview across every project, with a
/// "View all" link to <see cref="BuildsModel"/> for the full, filterable, sortable list. View,
/// download, and upload only -- this is the human-facing side of the build archive. Delete and
/// update live exclusively behind the API (<c>/api/builds</c>), for the dev team's GUI to use;
/// intentionally not exposed here.
/// </summary>
public class IndexModel : PageModel
{
    private const int RecentBuildsCount = 10;

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

    /// <summary>One card per project, ordered by most recently active first.</summary>
    public IReadOnlyList<ProjectSummary> Projects { get; private set; } = Array.Empty<ProjectSummary>();

    /// <summary>The most recent builds across every project, for the landing page's preview --
    /// "View all" goes to <see cref="BuildsModel"/> for the complete, filterable list.</summary>
    public BuildsTableViewModel? RecentBuilds { get; private set; }

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

        var builds = _buildRepository.ListBuilds(BuildsRootPath)
            .Where(b => b.ListInHosting)
            .OrderByDescending(b => b.PublishedAtUtc)
            .ToList();

        Projects = builds
            .GroupBy(b => b.ProjectName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProjectSummary(g.Key, g.Count(), g.OrderByDescending(b => b.PublishedAtUtc).First()))
            .OrderByDescending(p => p.MostRecentBuild!.PublishedAtUtc)
            .ToList();

        var recent = builds.Take(RecentBuildsCount).ToList();
        RecentBuilds = new BuildsTableViewModel
        {
            Builds = recent,
            BuildsRootPath = BuildsRootPath,
            ReleaseNotesByPath = BuildDisplayHelpers.BuildReleaseNotesMap(BuildsRootPath, recent),
        };
    }
}
