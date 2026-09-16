using System.Linq;
using PublishTool.Core.Models;
using PublishTool.Core.Services.BuildRunners;

namespace PublishTool.Core.Services.UnitTestRunners;

/// <summary>Runs <c>npm test</c> in an Angular or Android (Capacitor/Cordova) project's root --
/// both are ultimately a web frontend, so this is the same for either type, same reasoning as
/// <see cref="BuildRunners.AngularBuildRunner"/> not needing framework-specific branching. Expects
/// the project's own test setup (a Karma/Jest JUnit reporter, most commonly) to have already
/// written a JUnit-format XML report to <see cref="ResultFileRelativePath"/> -- PublishTool doesn't
/// inject or configure a reporter itself. If that file never shows up (project has no test script,
/// or its reporter isn't configured to write there), this is treated as "nothing to report," not a
/// failure -- see <see cref="IUnitTestRunner"/>'s remarks on why unit tests never gate publish.
///
/// Unlike .NET, there's no per-suite project path concept here -- <c>npm test</c> is the one
/// mechanism, so any configured <see cref="ProjectConfig.TestSuites"/> entry (regardless of name or
/// <see cref="TestSuiteConfig.ProjectPath"/>, which is meaningless for this project type) just means
/// "this project wants its one implicit test run included."</summary>
public sealed class JsUnitTestRunner : IUnitTestRunner
{
    /// <summary>Fixed by convention (not user-configurable) -- your project's test runner
    /// (Karma/Jest, etc.) needs a JUnit reporter configured to write exactly here.</summary>
    public const string ResultFileRelativePath = "test-results/junit.xml";

    private static readonly TestSuiteConfig ImplicitSuite = new() { Name = "npm test" };

    public IReadOnlyList<TestSuiteConfig> GetConfiguredSuites(ProjectConfig project) =>
        project.TestSuites.Count > 0 && ResolveProjectRoot(project) is not null
            ? new[] { ImplicitSuite }
            : Array.Empty<TestSuiteConfig>();

    public async Task<UnitTestRunResult> RunSuiteAsync(ProjectConfig project, TestSuiteConfig suite, UnitTestContext context, CancellationToken ct)
    {
        var projectRoot = ResolveProjectRoot(project)
            ?? throw new InvalidOperationException($"'{project.Name}' has no project root path configured.");

        var resultPath = Path.Combine(projectRoot, ResultFileRelativePath);
        if (File.Exists(resultPath))
        {
            // A stale report from a previous run would otherwise look like this run's result if
            // npm test fails before the reporter gets a chance to overwrite it.
            File.Delete(resultPath);
        }

        context.Output.Stage("Running npm test...");
        var exitCode = await ShellCommandRunner.RunAsync("npm test", projectRoot, context.Output, ct);

        if (!File.Exists(resultPath))
        {
            context.Output.Info(
                $"npm test exited with code {exitCode} but no JUnit report was found at '{resultPath}' -- " +
                "nothing to report (configure a JUnit reporter to write there if you want results included).");
            return new UnitTestRunResult(exitCode == 0, Array.Empty<UnitTestCaseResult>());
        }

        var cases = JUnitXmlTestResultParser.Parse(resultPath);
        return new UnitTestRunResult(exitCode == 0, cases);
    }

    private static string? ResolveProjectRoot(ProjectConfig project) => project.ProjectType switch
    {
        ProjectType.Angular => project.Angular?.ProjectRootPath,
        ProjectType.Android => project.Android?.ProjectRootPath,
        _ => null,
    };
}
