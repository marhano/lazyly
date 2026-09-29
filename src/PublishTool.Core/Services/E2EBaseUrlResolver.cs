using PublishTool.Core.Models;

namespace PublishTool.Core.Services;

/// <summary>
/// Resolves a BASE_URL for an E2E test suite from a target environment (see
/// <see cref="SharedTestSuiteConfig.EnvironmentName"/>) -- this is what lets PublishTool point an E2E
/// suite at a real, reachable deployment instead of the "https://localhost:xxxxx" a test project
/// typically defaults to for a developer's own F5/IIS Express session. Only ever looks at
/// <see cref="ProjectConfig.RemoteEnvironments"/> (the one environment list that's genuinely shared/
/// team-wide -- Local environments are per-dev and wouldn't mean the same thing to whoever reads the
/// Shared setting that names one).
/// </summary>
public static class E2EBaseUrlResolver
{
    /// <summary>
    /// <paramref name="environmentNameOverride"/>: which environment to resolve against -- null falls
    /// back to the project's own configured default (<see cref="SharedTestSuiteConfig.EnvironmentName"/>).
    /// Non-null lets a caller point at a *different* environment for just this one run, e.g. the
    /// Projects tab's tests dialog prompting which environment to run E2E against on demand, rather
    /// than always using whatever's configured on the project.
    /// <paramref name="hostOverride"/>: null means "resolve to localhost" -- correct when the test
    /// itself is running on the SAME machine the target environment is deployed on (a dev server
    /// running its own Remote environment). Pass the dev server's actual host when the test runs
    /// elsewhere (e.g. a developer's own machine targeting the dev server's Remote environment during
    /// a local publish or on-demand run) -- see <see cref="AppSettings.RemoteHostingUrl"/>.
    /// A binding's own <see cref="IisBinding.HostName"/>, if set, always wins over both.
    /// </summary>
    public static string? Resolve(ProjectConfig project, string? environmentNameOverride, string? hostOverride)
    {
        var environmentName = environmentNameOverride
            ?? project.TestSuiteTypes.FirstOrDefault(t => t.Type == TestSuiteType.E2E)?.EnvironmentName;
        if (string.IsNullOrWhiteSpace(environmentName))
        {
            return null;
        }

        var environment = project.RemoteEnvironments
            .FirstOrDefault(e => string.Equals(e.Name, environmentName, StringComparison.OrdinalIgnoreCase));
        var binding = environment?.Bindings.FirstOrDefault();
        if (binding is null)
        {
            return null;
        }

        var host = !string.IsNullOrWhiteSpace(binding.HostName) ? binding.HostName : (hostOverride ?? "localhost");
        return $"{binding.Protocol}://{host}:{binding.Port}";
    }

    /// <summary>Environment variables to run one suite with -- currently only ever populates
    /// anything for the E2E suite (<c>BASE_URL</c>, the convention this codebase's own real E2E
    /// project already reads as a legacy alias for <c>Playwright:BaseUrl</c> -- see its
    /// PlaywrightTestConfig). Returns null (no overrides) for any other suite, including Unit Test --
    /// a project's Unit Test suite is a genuinely separate concern from E2E even when both happen to
    /// be browser-driven (e.g. an older Selenium-based project registered under the Unit Test slot):
    /// it is NOT necessarily meant to target the same environment/URL, so it must keep whatever
    /// target it already resolves on its own (its own config default, typically the dev's own local
    /// instance) rather than being redirected here.
    /// <paramref name="baseUrlOverride"/>: a full URL that bypasses environment/binding resolution
    /// entirely -- the Publish tab's own explicit "E2E test URL" field, for a one-off target that
    /// doesn't correspond to any registered environment at all. Wins over
    /// <paramref name="environmentNameOverride"/> when both are given (they're offered by different,
    /// mutually exclusive UI surfaces in practice, but a direct URL is the more explicit ask).</summary>
    public static IReadOnlyDictionary<string, string>? EnvironmentVariablesFor(
        TestSuiteConfig suite, ProjectConfig project, string? hostOverride, string? environmentNameOverride = null, string? baseUrlOverride = null)
    {
        if (!string.Equals(suite.Name, TestSuiteTypeNames.DisplayName(TestSuiteType.E2E), StringComparison.Ordinal))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(baseUrlOverride))
        {
            return new Dictionary<string, string> { ["BASE_URL"] = baseUrlOverride };
        }

        var baseUrl = Resolve(project, environmentNameOverride, hostOverride);
        return baseUrl is null ? null : new Dictionary<string, string> { ["BASE_URL"] = baseUrl };
    }
}
