using Microsoft.AspNetCore.Mvc.RazorPages;
using PublishTool.Core.Models;
using PublishTool.Core.Services;

namespace PublishTool.Hosting.Pages;

/// <summary>Every build, across every project -- the "View all" destination from the landing
/// page's "Recent builds" section, and the direct successor to what the landing page itself used
/// to show before it became project cards + a recent-builds preview.</summary>
public class BuildsModel : PageModel
{
    private readonly IConfiguration _configuration;
    private readonly BuildRepository _buildRepository = new();

    public BuildsModel(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public bool BuildsRootNotConfigured { get; private set; }

    public bool BuildsRootNotAccessible { get; private set; }

    public string BuildsRootPath { get; private set; } = string.Empty;

    public string RunningAs { get; private set; } = Environment.UserName;

    public BuildsTableViewModel? Table { get; private set; }

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

        var projectNames = builds
            .Select(b => b.ProjectName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Table = new BuildsTableViewModel
        {
            Builds = builds,
            BuildsRootPath = BuildsRootPath,
            ProjectNames = projectNames,
            ReleaseNotesByPath = BuildDisplayHelpers.BuildReleaseNotesMap(BuildsRootPath, builds),
        };
    }
}
