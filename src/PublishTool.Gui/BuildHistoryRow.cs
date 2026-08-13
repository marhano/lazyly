namespace PublishTool.Gui;

/// <summary>
/// One row in the Projects tab's build-history grid -- unifies a local <c>BuildManifest</c> and a
/// remote <c>BuildSummaryDto</c> into the same shape, since the grid doesn't care which mode
/// produced the data, only the per-row action handlers (<see cref="ManifestPath"/> for local,
/// <see cref="RemoteManifestPath"/> for remote) do.
/// </summary>
public sealed class BuildHistoryRow
{
    public required string Version { get; init; }

    public required DateTimeOffset PublishedAtUtc { get; init; }

    public required string PublishedBy { get; init; }

    public bool IsLatest { get; init; }

    public bool ListInHosting { get; init; }

    /// <summary>Absolute path to this build's manifest on the local machine. Set only in local mode.</summary>
    public string? ManifestPath { get; init; }

    /// <summary>Absolute path to this build's zip on the local machine. Set only in local mode.</summary>
    public string? ZipPath { get; init; }

    /// <summary>Manifest path relative to the dev server's BuildsRoot, as returned by the Remote
    /// Build Hosting API. Set only in remote mode.</summary>
    public string? RemoteManifestPath { get; init; }

    public bool IsRemote => RemoteManifestPath is not null;
}
