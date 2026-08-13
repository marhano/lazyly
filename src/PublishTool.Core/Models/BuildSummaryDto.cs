namespace PublishTool.Core.Models;

/// <summary>
/// A build as returned by the Remote Build Hosting API's list/upload endpoints -- deliberately
/// distinct from <see cref="BuildManifest"/>, whose Zip/ReleaseNotes paths are absolute paths on
/// whichever machine wrote them and must never be exposed to a remote client. All paths here are
/// relative to the server's BuildsRoot, suitable for passing straight back as the API's
/// <c>path</c> query parameter (download/update/delete).
/// </summary>
public sealed class BuildSummaryDto
{
    public required string ProjectName { get; set; }

    public required string Version { get; set; }

    public required DateTimeOffset PublishedAtUtc { get; set; }

    public required string PublishedBy { get; set; }

    public bool ListInHosting { get; set; } = true;

    public bool IsLatest { get; set; }

    /// <summary>Relative path identifying this exact build -- pass back as the <c>path</c> query
    /// parameter to update/delete it, since project+version alone isn't guaranteed unique.</summary>
    public required string ManifestPath { get; set; }

    public required string ZipPath { get; set; }

    public string? ReleaseNotesPath { get; set; }
}
