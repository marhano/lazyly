using System.IO.Compression;
using PublishTool.Core;
using PublishTool.Core.Models;
using PublishTool.Core.Services;
using PublishTool.Core.Services.UnitTestRunners;

namespace PublishTool.Hosting;

/// <summary>
/// Runs one build's own bundled test suites on this server -- the server-side counterpart to running
/// tests locally, letting a non-developer, remote-only user trigger a real test run without needing
/// the project's source, git, or any checkout on their own machine (or on this server). The bundle
/// (<see cref="BuildManifest.TestBundlePath"/>) was built/published by <see cref="Publisher"/> on the
/// publishing dev's own machine at publish time and uploaded alongside the build -- this just unzips
/// it and hands the extracted assemblies to the exact same <see cref="UnitTestOrchestrator"/>/
/// <see cref="DotNetUnitTestRunner"/> used for local execution (via PublishTool.Core's project
/// reference), which already knows how to run an already-built assembly directly: <c>dotnet vstest</c>
/// for a modern suite (needs only the .NET SDK, no Visual Studio at all), or vstest.console.exe for a
/// classic one (bundled with this app -- see <c>PublishTool.Hosting.csproj</c>'s Microsoft.TestPlatform
/// reference). Playwright/E2E suites self-install their browsers here on first use.
/// </summary>
internal sealed class RemoteTestRunnerService
{
    /// <summary><paramref name="suiteType"/> null runs every suite the build's bundle carries; a
    /// specific type restricts the run to just that one -- used by the Projects tab's test-run
    /// dialog to run/view each suite independently instead of always running everything at once.
    /// <paramref name="environmentNameOverride"/> overrides which environment an E2E suite targets
    /// for just this run (the Projects tab's environment picker) -- null falls back to the project's
    /// own configured default, same as before this parameter existed.
    /// </summary>
    public async Task<UnitTestOrchestratorResult?> RunAsync(
        string buildsRoot, string projectName, string version, TestSuiteType? suiteType, string? environmentNameOverride,
        string workingDir, IOutputSink output, CancellationToken ct)
    {
        var existing = new BuildRepository().FindBuild(buildsRoot, projectName, version)
            ?? throw new InvalidOperationException($"No build found for '{projectName}' v{version} on this server.");

        if (string.IsNullOrWhiteSpace(existing.Manifest.TestBundlePath) || !File.Exists(existing.Manifest.TestBundlePath))
        {
            throw new InvalidOperationException(
                $"'{projectName}' v{version} has no test bundle on this server -- it may predate this feature, or its test " +
                "suites failed to build/publish at publish time (republish to try again).");
        }

        if (existing.Manifest.TestBundleSuites.Count == 0)
        {
            throw new InvalidOperationException($"'{projectName}' v{version}'s test bundle has no suites recorded.");
        }

        output.Stage("Extracting test bundle...");
        var extractDir = Path.Combine(workingDir, "bundle");
        Directory.CreateDirectory(extractDir);
        await Task.Run(() => ZipFile.ExtractToDirectory(existing.Manifest.TestBundlePath, extractDir), ct);

        // The build's own bundle only knows suite NAMES ("Unit Test"/"E2E") and where their
        // assemblies live -- an E2E suite's target environment is a SHARED, project-level setting
        // (SharedTestSuiteConfig.EnvironmentName), not something baked into the bundle, so it's
        // looked up here from the project's current shared config rather than the build's manifest.
        // A project since removed from the registry (or a server predating this feature's shared
        // config) just means no environment override is found -- degrades to "no BASE_URL", not a
        // failure.
        var sharedProject = new SharedProjectStore().GetProject(buildsRoot, projectName);

        var testSuiteTypes = new List<SharedTestSuiteConfig>();
        var testSuitePaths = new List<LocalTestSuiteConfig>();
        foreach (var bundleSuite in existing.Manifest.TestBundleSuites)
        {
            if (!TestSuiteTypeNames.TryParseDisplayName(bundleSuite.Name, out var type))
            {
                continue;
            }

            testSuiteTypes.Add(new SharedTestSuiteConfig
            {
                Type = type,
                EnvironmentName = type == TestSuiteType.E2E && environmentNameOverride is not null
                    ? environmentNameOverride
                    : sharedProject?.TestSuiteTypes.FirstOrDefault(t => t.Type == type)?.EnvironmentName,
            });
            testSuitePaths.Add(new LocalTestSuiteConfig
            {
                Type = type,
                ProjectPath = Path.Combine(extractDir, UnitTestOrchestrator.SanitizeForPath(bundleSuite.Name), bundleSuite.AssemblyFileName),
            });
        }

        // Same shape UnitTestOrchestrator/DotNetUnitTestRunner already know how to run locally --
        // pointing a suite's path at an already-built .dll (not a .csproj) is exactly what tells
        // DotNetUnitTestRunner to skip straight to running it, no build step at all.
        var effectiveProject = new ProjectConfig
        {
            Name = projectName,
            ProjectType = ProjectType.DotNet,
            TestSuiteTypes = testSuiteTypes,
            TestSuitePaths = testSuitePaths,
            RemoteEnvironments = sharedProject?.RemoteEnvironments ?? new List<DeploymentEnvironment>(),
        };

        // hostOverride: null -> E2EBaseUrlResolver resolves to "localhost", which is correct here --
        // a Remote environment is always deployed by this same Hosting server, on this same machine.
        return await UnitTestOrchestrator.RunAllAsync(
            effectiveProject, workingDir, output, ct,
            suiteNameFilter: suiteType is null ? null : new[] { TestSuiteTypeNames.DisplayName(suiteType.Value) },
            e2eHostOverride: null);
    }
}
