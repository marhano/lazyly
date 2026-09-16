namespace PublishTool.Core.Models;

public sealed class BuildManifest
{
    public required string ProjectName { get; set; }

    public required string Version { get; set; }

    public required DateTimeOffset PublishedAtUtc { get; set; }

    public required string PublishedBy { get; set; }

    public required string ZipPath { get; set; }

    /// <summary>
    /// Copied from the project's ListInHosting setting at publish time, so the hosting site can
    /// filter by reading manifests alone -- not required, so older manifests without this field
    /// still deserialize fine and default to listed.
    /// </summary>
    public bool ListInHosting { get; set; } = true;

    /// <summary>
    /// Whether this is the project's flagged "latest release" build, shown as "(latest)" next to
    /// its name/version on the hosting site. At most one build per project should have this set --
    /// enforced by <see cref="Services.BuildRepository.SetLatest"/>, which is the only supported
    /// way to set it to true (it un-flags whatever build previously held it for the same project).
    /// </summary>
    public bool IsLatest { get; set; }

    /// <summary>
    /// Absolute path to this build's release notes .txt, or null if none was generated (the
    /// project had no Project ID set at publish time, or this build predates the feature).
    /// Deliberately kept out of the zip -- it's meant to be browsable/downloadable on its own
    /// from the build-hosting site, not bundled inside the deployed package.
    /// </summary>
    public string? ReleaseNotesPath { get; set; }

    /// <summary>
    /// Absolute path to this build's unit test report (.xlsx), or null if unit tests weren't run
    /// (or produced no test cases) for this publish. Same treatment as <see cref="ReleaseNotesPath"/>
    /// -- a sibling artifact kept out of the zip so it's independently browsable/downloadable from
    /// the build-hosting site, not something a deployed app carries around with it.
    /// </summary>
    public string? UnitTestReportPath { get; set; }

    /// <summary>
    /// The app-config key/value settings (e.g. Web.config appSettings) this build was published
    /// with, if the project uses app config -- so re-selecting this version in the Publish tab
    /// shows exactly what was published, instead of whatever the config file currently has on
    /// disk (which may have since moved on to a newer version).
    /// </summary>
    public Dictionary<string, string>? AppConfigSettings { get; set; }

    /// <summary>
    /// Whether the project had any <see cref="ProjectConfig.TestSuites"/> configured at the moment
    /// this build was published. Defaults to false, so a build archived before the test-suites
    /// feature existed (or an older manifest with no such field at all) correctly reports "no test
    /// suites" -- test status is per-build, not per-project, since a build's own source state is
    /// what a test run actually reflects: an older build published before a suite was added (or
    /// before this GUI feature shipped at all) genuinely never had a chance to run it, so its
    /// build-history row shouldn't offer "Run Tests" or claim any result for it.
    /// </summary>
    public bool HasTestSuites { get; set; }

    /// <summary>
    /// Absolute path to a zip containing each configured <see cref="ProjectConfig.TestSuites"/>
    /// entry's built test assembly (one subfolder per suite, named after the suite) -- built/published
    /// at publish time on the publishing dev's own machine (which has the real source and tooling)
    /// and uploaded as a sibling artifact, same treatment as <see cref="UnitTestReportPath"/>. This is
    /// what lets remote (dev-server) test execution work with no git, no checkout, and no source on
    /// the server at all: the server just unzips this and runs it. Null if the project had no test
    /// suites, a suite failed to build/publish at publish time, or this build predates the feature.
    /// </summary>
    public string? TestBundlePath { get; set; }

    /// <summary>Which suite is which inside <see cref="TestBundlePath"/> -- see
    /// <see cref="TestBundleSuiteEntry"/>. Empty if <see cref="TestBundlePath"/> is null.</summary>
    public List<TestBundleSuiteEntry> TestBundleSuites { get; set; } = new();
}
