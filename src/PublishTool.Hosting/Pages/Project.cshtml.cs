using Microsoft.AspNetCore.Mvc.RazorPages;
using PublishTool.Core.Models;
using PublishTool.Core.Services;

namespace PublishTool.Hosting.Pages;

/// <summary>One project's builds -- the destination when clicking a project card on the landing
/// page. Same table as <see cref="BuildsModel"/>, just pre-filtered and with the project-filter
/// dropdown hidden (see <see cref="BuildsTableViewModel.ProjectNames"/>) since it'd be redundant.</summary>
public class ProjectModel : PageModel
{
    private readonly IConfiguration _configuration;
    private readonly BuildRepository _buildRepository = new();

    public ProjectModel(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public bool BuildsRootNotConfigured { get; private set; }

    public bool BuildsRootNotAccessible { get; private set; }

    public string BuildsRootPath { get; private set; } = string.Empty;

    public string RunningAs { get; private set; } = Environment.UserName;

    public string ProjectName { get; private set; } = string.Empty;

    public BuildsTableViewModel? Table { get; private set; }

    public void OnGet(string name)
    {
        ProjectName = name;
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
            .Where(b => b.ListInHosting && string.Equals(b.ProjectName, name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(b => b.PublishedAtUtc)
            .ToList();

        Table = new BuildsTableViewModel
        {
            Builds = builds,
            BuildsRootPath = BuildsRootPath,
            ReleaseNotesByPath = BuildDisplayHelpers.BuildReleaseNotesMap(BuildsRootPath, builds),
        };
    }
}
