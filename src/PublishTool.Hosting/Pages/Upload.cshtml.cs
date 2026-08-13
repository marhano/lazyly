using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PublishTool.Core.Models;
using PublishTool.Core.Services;

namespace PublishTool.Hosting.Pages;

/// <summary>
/// Lets a team member upload a build straight through the browser -- for people who don't have
/// (or don't want) publish access to the shared build storage that PublishTool itself writes to.
/// Produces the exact same zip + manifest.json (+ optional release notes .txt) shape as a normal
/// publish, so uploaded builds are indistinguishable from published ones once listed.
/// </summary>
public class UploadModel : PageModel
{
    private readonly IConfiguration _configuration;
    private readonly BuildRepository _buildRepository = new();

    public UploadModel(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    [BindProperty]
    public string ProjectName { get; set; } = string.Empty;

    [BindProperty]
    public string Version { get; set; } = string.Empty;

    [BindProperty]
    public string UploadedBy { get; set; } = string.Empty;

    [BindProperty]
    public bool ListInHosting { get; set; } = true;

    /// <summary>Only one build per project can be latest -- <see cref="BuildRepository.SetLatest"/>
    /// un-flags whichever build previously held it, so this is the only path that can set it true.</summary>
    [BindProperty]
    public bool MarkAsLatest { get; set; }

    [BindProperty]
    public IFormFile? UploadedZip { get; set; }

    [BindProperty]
    public bool IncludeReleaseNotes { get; set; }

    [BindProperty]
    public string? Reference { get; set; }

    [BindProperty]
    public DateOnly ReleaseDate { get; set; } = DateOnly.FromDateTime(DateTime.Now);

    [BindProperty]
    public string? Features { get; set; }

    [BindProperty]
    public string? Fixes { get; set; }

    [BindProperty]
    public string? OtherUpdates { get; set; }

    [BindProperty]
    public string? BacklogItems { get; set; }

    public IReadOnlyList<string> ExistingProjectNames { get; private set; } = Array.Empty<string>();

    public string? ErrorMessage { get; private set; }

    public bool BuildsRootNotConfigured { get; private set; }

    public bool BuildsRootNotAccessible { get; private set; }

    public string MaxUploadSizeDisplay => FormatBytes(_configuration.GetValue<long?>("MaxUploadBytes") ?? 524_288_000L);

    public void OnGet()
    {
        LoadExistingProjectNames();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var buildsRoot = _configuration["BuildsRoot"];
        if (string.IsNullOrWhiteSpace(buildsRoot))
        {
            BuildsRootNotConfigured = true;
            return Page();
        }

        if (!Directory.Exists(buildsRoot))
        {
            BuildsRootNotAccessible = true;
            return Page();
        }

        LoadExistingProjectNames();

        ProjectName = ProjectName.Trim();
        Version = Version.Trim();
        UploadedBy = UploadedBy.Trim();

        if (string.IsNullOrWhiteSpace(ProjectName) || string.IsNullOrWhiteSpace(Version) || string.IsNullOrWhiteSpace(UploadedBy))
        {
            ErrorMessage = "Project name, version, and your name are required.";
            return Page();
        }

        if (!UploadValidation.IsValidPathSegment(ProjectName) || !UploadValidation.IsValidPathSegment(Version))
        {
            ErrorMessage = "Project name and version can't contain \\ / : * ? \" < > | and can't be \".\" or \"..\".";
            return Page();
        }

        if (UploadedZip is null || UploadedZip.Length == 0)
        {
            ErrorMessage = "Select a .zip file to upload.";
            return Page();
        }

        if (!Path.GetExtension(UploadedZip.FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ErrorMessage = "Only .zip files are accepted.";
            return Page();
        }

        if (IncludeReleaseNotes && string.IsNullOrWhiteSpace(Reference))
        {
            ErrorMessage = "Reference is required when adding release notes.";
            return Page();
        }

        // Defense in depth beyond IsValidPathSegment -- confirm the resolved path still lands
        // inside BuildsRoot, the same check every other path-taking endpoint applies.
        if (!SafeBuildPath.IsValidProjectName(buildsRoot, ProjectName))
        {
            ErrorMessage = "Invalid project name.";
            return Page();
        }

        // Same version uploaded again overwrites in place instead of creating a duplicate --
        // matches how a republish of the same version already behaves.
        var paths = _buildRepository.ResolvePaths(buildsRoot, ProjectName, Version);

        await using (var fileStream = System.IO.File.Create(paths.ZipPath))
        {
            await UploadedZip.CopyToAsync(fileStream);
        }

        if (!UploadValidation.IsValidZip(paths.ZipPath))
        {
            System.IO.File.Delete(paths.ZipPath);
            ErrorMessage = "The uploaded file isn't a valid .zip archive.";
            return Page();
        }

        string? releaseNotesPath = null;
        if (IncludeReleaseNotes)
        {
            var content = ReleaseNotesFormatter.Format(new ReleaseNotesEntry
            {
                Reference = Reference!.Trim(),
                Version = Version,
                Date = ReleaseDate,
                Features = SplitLines(Features),
                Fixes = SplitLines(Fixes),
                OtherUpdates = SplitLines(OtherUpdates),
                BacklogItems = SplitLines(BacklogItems),
            });

            _buildRepository.WriteReleaseNotes(paths.ReleaseNotesPath, content);
            releaseNotesPath = paths.ReleaseNotesPath;
        }

        _buildRepository.WriteManifest(paths.ManifestPath, new BuildManifest
        {
            ProjectName = ProjectName,
            Version = Version,
            PublishedAtUtc = DateTimeOffset.UtcNow,
            PublishedBy = UploadedBy,
            ZipPath = paths.ZipPath,
            ListInHosting = ListInHosting,
            ReleaseNotesPath = releaseNotesPath,
            IsLatest = MarkAsLatest,
        });

        if (MarkAsLatest)
        {
            _buildRepository.SetLatest(buildsRoot, ProjectName, paths.ManifestPath);
        }

        TempData["StatusMessage"] = $"Uploaded {ProjectName} v{Version}.";
        return RedirectToPage("/Index");
    }

    private void LoadExistingProjectNames()
    {
        var buildsRoot = _configuration["BuildsRoot"];
        if (!string.IsNullOrWhiteSpace(buildsRoot))
        {
            ExistingProjectNames = _buildRepository.ListProjectNames(buildsRoot);
        }
    }

    private static List<string> SplitLines(string? text) =>
        (text ?? string.Empty)
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

    private static string FormatBytes(long bytes)
    {
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
