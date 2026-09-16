using PublishTool.Core.Models;

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

    /// <summary>Whether the "Run Tests" action should be offered for THIS build -- from that build's
    /// own manifest (<c>BuildManifest.HasTestSuites</c>/<c>BuildSummaryDto.HasTestSuites</c>,
    /// snapshotted at publish time), not the project's current live configuration. A build published
    /// before test suites were added to the project (or before this GUI feature existed at all)
    /// always reports false here, since older manifests never had this field -- it genuinely never
    /// had a chance to run them, so it shouldn't offer to, or claim any result for it.</summary>
    public bool HasTestSuites { get; init; }

    /// <summary>This exact build's own last-known test run result (matched by project name +
    /// version -- see <see cref="TestRunStatus"/>'s remarks). Null if tests have never been run for
    /// this specific build.</summary>
    public TestRunStatus? TestStatus { get; init; }

    public string TestResultDisplay =>
        TestStatus is null ? "Not started" : TestStatus.Passed ? "Passed" : "Failed";

    /// <summary>Absolute path to this build's unit test report (.xlsx) on the local machine, or null
    /// if this build predates the report or never produced test cases. Set only in local mode.</summary>
    public string? UnitTestReportPath { get; init; }

    /// <summary>Unit test report path relative to the dev server's BuildsRoot. Set only in remote
    /// mode -- downloaded the same way <see cref="RemoteZipPath"/> is.</summary>
    public string? RemoteUnitTestReportPath { get; init; }

    public bool HasUnitTestReport => UnitTestReportPath is not null || RemoteUnitTestReportPath is not null;
}
