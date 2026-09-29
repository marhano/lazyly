namespace PublishTool.Core.Models;

/// <summary>The team-wide half of a project's test suite configuration -- WHICH suite types exist
/// (at most one entry per <see cref="TestSuiteType"/>), and for an E2E suite, WHICH of the project's
/// <see cref="ProjectConfig.RemoteEnvironments"/> to test against. Deliberately doesn't carry a local
/// filesystem path -- see <see cref="LocalTestSuiteConfig"/> for that half, which lives in
/// <see cref="LocalProjectOverrides"/> instead, same split as every other local-path field in this
/// model (<see cref="ProjectConfig.CsprojPath"/> etc.).</summary>
public sealed class SharedTestSuiteConfig
{
    public required TestSuiteType Type { get; set; }

    /// <summary>Only meaningful when <see cref="Type"/> is <see cref="TestSuiteType.E2E"/> -- the
    /// name of one of <see cref="ProjectConfig.RemoteEnvironments"/> to resolve a target URL from at
    /// run time (see <see cref="Services.E2EBaseUrlResolver"/>), so the suite hits a real, reachable
    /// deployment instead of whatever localhost address its own test project happens to default to.
    /// Null means the suite runs with no URL override -- it falls back to its own project's default.
    /// </summary>
    public string? EnvironmentName { get; set; }
}
