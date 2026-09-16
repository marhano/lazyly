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
/// classic one (needs Visual Studio/Build Tools installed on this server -- same requirement a dev's
/// own machine has for local execution, just here instead). Playwright/E2E suites still need their
/// browsers installed here too (a one-time <c>playwright.ps1 install</c>), same as any dev machine.
/// </summary>
internal sealed class RemoteTestRunnerService
{
    public async Task<UnitTestOrchestratorResult?> RunAsync(
        string buildsRoot, string projectName, string version, string workingDir, IOutputSink output, CancellationToken ct)
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

        // Same shape UnitTestOrchestrator/DotNetUnitTestRunner already know how to run locally --
        // pointing TestSuiteConfig.ProjectPath at an already-built .dll (not a .csproj) is exactly
        // what tells DotNetUnitTestRunner to skip straight to running it, no build step at all.
        var effectiveProject = new ProjectConfig
        {
            Name = projectName,
            ProjectType = ProjectType.DotNet,
            TestSuites = existing.Manifest.TestBundleSuites
                .Select(s => new TestSuiteConfig
                {
                    Name = s.Name,
                    ProjectPath = Path.Combine(extractDir, UnitTestOrchestrator.SanitizeForPath(s.Name), s.AssemblyFileName),
                })
                .ToList(),
        };

        return await UnitTestOrchestrator.RunAllAsync(effectiveProject, workingDir, output, ct);
    }
}
