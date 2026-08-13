using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PublishTool.Core.Models;
using PublishTool.Core.Services;

namespace PublishTool.Hosting.Pages;

/// <summary>
/// Alternative to <see cref="UploadModel"/>'s manual-entry form -- for a dev who already has a
/// local publish's three output files (zip, manifest.json, and optionally a release notes .txt)
/// sitting on disk and just needs them in the shared build archive. All the metadata a manual
/// upload asks for by hand (project name, version, who published it, release notes) already lives
/// in the manifest, so this reads it from there instead of asking the dev to retype it.
/// </summary>
public class UploadBuildModel : PageModel
{
    private readonly IConfiguration _configuration;
    private readonly BuildRepository _buildRepository = new();

    public UploadBuildModel(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    [BindProperty]
    public IFormFile? BuildZip { get; set; }

    [BindProperty]
    public IFormFile? ManifestFile { get; set; }

    [BindProperty]
    public IFormFile? ReleaseNotesFile { get; set; }

    /// <summary>Only one build per project can be latest -- <see cref="BuildRepository.SetLatest"/>
    /// un-flags whichever build previously held it, so this is the only path that can set it true.
    /// Deliberately not read from the uploaded manifest -- whether to promote a build to "latest"
    /// is a decision made here at upload time, not something baked in from a local publish.</summary>
    [BindProperty]
    public bool MarkAsLatest { get; set; }

    public string? ErrorMessage { get; private set; }

    public bool BuildsRootNotConfigured { get; private set; }

    public bool BuildsRootNotAccessible { get; private set; }

    public string MaxUploadSizeDisplay => FormatBytes(_configuration.GetValue<long?>("MaxUploadBytes") ?? 524_288_000L);

    public void OnGet()
    {
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

        if (BuildZip is null || BuildZip.Length == 0)
        {
            ErrorMessage = "Select the build's .zip file.";
            return Page();
        }

        if (!Path.GetExtension(BuildZip.FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ErrorMessage = "The build file must be a .zip.";
            return Page();
        }

        if (ManifestFile is null || ManifestFile.Length == 0)
        {
            ErrorMessage = "Select the build's manifest.json file.";
            return Page();
        }

        if (ReleaseNotesFile is not null && ReleaseNotesFile.Length > 0 &&
            !Path.GetExtension(ReleaseNotesFile.FileName).Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            ErrorMessage = "Release notes must be a .txt file.";
            return Page();
        }

        BuildManifest? manifest;
        try
        {
            await using var manifestStream = ManifestFile.OpenReadStream();
            manifest = await JsonSerializer.DeserializeAsync<BuildManifest>(manifestStream);
        }
        catch (JsonException)
        {
            manifest = null;
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.ProjectName) || string.IsNullOrWhiteSpace(manifest.Version))
        {
            ErrorMessage = "That doesn't look like a valid manifest.json -- it must be the one PublishTool wrote alongside this build's zip.";
            return Page();
        }

        var projectName = manifest.ProjectName.Trim();
        var version = manifest.Version.Trim();

        if (!UploadValidation.IsValidPathSegment(projectName) || !UploadValidation.IsValidPathSegment(version))
        {
            ErrorMessage = "The manifest's project name or version contains characters that aren't allowed in a file path.";
            return Page();
        }

        // Defense in depth beyond IsValidPathSegment -- confirm the resolved path still lands
        // inside BuildsRoot, the same check /download and the manual upload apply to their own
        // path inputs.
        var normalizedRoot = Path.GetFullPath(buildsRoot) + Path.DirectorySeparatorChar;
        var projectDir = Path.GetFullPath(Path.Combine(buildsRoot, projectName));
        if (!projectDir.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            ErrorMessage = "Invalid project name.";
            return Page();
        }

        var paths = _buildRepository.ReservePaths(buildsRoot, projectName, version);

        await using (var fileStream = System.IO.File.Create(paths.ZipPath))
        {
            await BuildZip.CopyToAsync(fileStream);
        }

        if (!UploadValidation.IsValidZip(paths.ZipPath))
        {
            System.IO.File.Delete(paths.ZipPath);
            ErrorMessage = "The uploaded build file isn't a valid .zip archive.";
            return Page();
        }

        string? releaseNotesPath = null;
        if (ReleaseNotesFile is not null && ReleaseNotesFile.Length > 0)
        {
            await using var notesStream = System.IO.File.Create(paths.ReleaseNotesPath);
            await ReleaseNotesFile.CopyToAsync(notesStream);
            releaseNotesPath = paths.ReleaseNotesPath;
        }

        // ZipPath/ReleaseNotesPath in the uploaded manifest point at the dev's own local machine
        // and mean nothing here -- everything else (who/when/whether it's listed/app config) comes
        // straight from their manifest instead of asking them to retype it.
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
            IsLatest = MarkAsLatest,
        });

        if (MarkAsLatest)
        {
            _buildRepository.SetLatest(buildsRoot, projectName, paths.ManifestPath);
        }

        TempData["UploadSuccessMessage"] = $"Uploaded {projectName} v{version} from build files.";
        return RedirectToPage("/Index");
    }

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
