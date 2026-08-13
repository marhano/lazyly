using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PublishTool.Hosting.Pages;

/// <summary>
/// Alternative to <see cref="UploadModel"/>'s manual-entry form -- for a dev who already has a
/// local publish's three output files (zip, manifest.json, and optionally a release notes .txt)
/// sitting on disk and just needs them in the shared build archive. All the metadata a manual
/// upload asks for by hand (project name, version, who published it, release notes) already lives
/// in the manifest, so this reads it from there instead of asking the dev to retype it.
/// Validation and file-writing live in <see cref="BuildUploadHandler"/>, shared with the
/// <c>POST /api/builds/upload</c> endpoint -- this page is just the browser-facing wrapper around it.
/// </summary>
public class UploadBuildModel : PageModel
{
    private readonly IConfiguration _configuration;
    private readonly BuildUploadHandler _handler = new();

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

    /// <summary>Only one build per project can be latest -- <see cref="PublishTool.Core.Services.BuildRepository.SetLatest"/>
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

        if (ManifestFile is null || ManifestFile.Length == 0)
        {
            ErrorMessage = "Select the build's manifest.json file.";
            return Page();
        }

        var hasReleaseNotes = ReleaseNotesFile is not null && ReleaseNotesFile.Length > 0;

        await using var zipStream = BuildZip.OpenReadStream();
        await using var manifestStream = ManifestFile.OpenReadStream();
        await using var releaseNotesStream = hasReleaseNotes ? ReleaseNotesFile!.OpenReadStream() : null;

        var result = await _handler.HandleAsync(buildsRoot, new BuildUploadRequest(
            zipStream, BuildZip.FileName,
            manifestStream,
            releaseNotesStream, hasReleaseNotes ? ReleaseNotesFile!.FileName : null,
            MarkAsLatest), HttpContext.RequestAborted);

        if (!result.Success)
        {
            ErrorMessage = result.ErrorMessage;
            return Page();
        }

        TempData["StatusMessage"] = $"Uploaded {result.ProjectName} v{result.Version} from build files.";
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
