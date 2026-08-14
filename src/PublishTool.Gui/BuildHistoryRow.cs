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

    /// <summary>Zip path relative to the dev server's BuildsRoot. Set only in remote mode -- used
    /// to download this build's zip when redeploying it to Local IIS instead of the dev server's
    /// own (the one cross-side case that can happen; see <see cref="MainWindow.DeployBuildButton_Click"/>).</summary>
    public string? RemoteZipPath { get; init; }

    public bool IsRemote => RemoteManifestPath is not null;

    /// <summary>Whether the "Deploy this version" action should even be offered for this row's
    /// project -- false (and the button hidden entirely) when the project has neither Local nor
    /// Remote IIS deployment available, so there's nowhere it could possibly deploy to.</summary>
    public bool CanDeploy { get; init; }
}
