using PublishTool.Core.Models;

namespace PublishTool.Core.Services.UnitTestRunners;

/// <summary>
/// Runs one of a project's configured test suites and reports the result, regardless of what kind
/// of project it is. One implementation per <see cref="ProjectType"/> (Angular and Android share
/// one, since both are ultimately "run npm test in the project root") -- <see cref="UnitTestRunnerRegistry"/>
/// is the extension point for adding more, mirroring <see cref="BuildRunners.IBuildRunner"/>/
/// <see cref="BuildRunners.BuildRunnerRegistry"/>.
///
/// Unlike a build, unit tests are always optional and never fail the publish -- see
/// <see cref="UnitTestOrchestrator"/>, the one caller of this interface, which only ever logs
/// whatever a run returns/throws and continues regardless. Implementations don't need to guard
/// against that themselves; throwing on a genuine problem (missing test project, tool not found,
/// non-zero exit code) is fine and expected -- the caller is what makes it non-fatal.
/// </summary>
public interface IUnitTestRunner
{
    /// <summary>The suites this project actually has runnable for this runner -- e.g. every
    /// <see cref="ProjectConfig.TestSuites"/> entry with a real <see cref="TestSuiteConfig.ProjectPath"/>
    /// set, for .NET; a single synthesized suite for Angular/Android (which have no per-suite path
    /// concept at all -- see <see cref="JsUnitTestRunner"/>). Empty means "nothing configured,"
    /// letting the caller skip cleanly with no report and no warning.</summary>
    IReadOnlyList<TestSuiteConfig> GetConfiguredSuites(ProjectConfig project);

    Task<UnitTestRunResult> RunSuiteAsync(ProjectConfig project, TestSuiteConfig suite, UnitTestContext context, CancellationToken ct);
}

/// <summary><paramref name="WorkingDir"/> is a fresh temp directory the runner may use for its own
/// scratch output (e.g. where <c>dotnet test</c> writes its .trx) -- unrelated to the build's own
/// staging directory, since tests run against source, not the published build output.
/// <paramref name="MsBuildPath"/> is an optional user-configured override for the classic .NET
/// Framework test-project path (see <see cref="DotNetUnitTestRunner"/>) -- null lets it fall back to
/// vswhere auto-detection, same as every other MSBuild.exe lookup in this app.</summary>
public sealed record UnitTestContext(string WorkingDir, IOutputSink Output, string? MsBuildPath = null);

/// <summary><paramref name="Cases"/> is every parsed test case from this one suite's run (empty if
/// the run produced none to report -- e.g. no JUnit file ever showed up). <paramref name="Passed"/>
/// is purely informational -- see the remarks on <see cref="IUnitTestRunner"/>, this never gates
/// publish success.</summary>
public sealed record UnitTestRunResult(bool Passed, IReadOnlyList<UnitTestCaseResult> Cases);
