using System.Linq;
using System.Xml.Linq;
using PublishTool.Core.Models;

namespace PublishTool.Core.Services.UnitTestRunners;

/// <summary>Runs a .NET test project (one of <see cref="ProjectConfig.TestSuites"/>) and parses the
/// resulting .trx. Each suite is its own project -- the registered project's own
/// <see cref="ProjectConfig.CsprojPath"/> is the app being published, not a test project.
///
/// Three shapes of "where the test code comes from" are supported, dispatched by
/// <see cref="RunSuiteAsync"/> purely from what <see cref="TestSuiteConfig.ProjectPath"/> points at:
///  - A modern SDK-style test project (<c>&lt;Project Sdk="Microsoft.NET.Sdk"&gt;</c>, referencing
///    <c>Microsoft.NET.Test.Sdk</c> via PackageReference) runs fine through <c>dotnet test</c> -- the
///    CLI does everything (restore, build, run, .trx) in one command.
///  - A classic .NET Framework test project using <c>packages.config</c> (old MSTest/NUnit/xUnit
///    project templates, still common for older ASP.NET Framework apps) is NOT SDK-style and doesn't
///    reference Microsoft.NET.Test.Sdk at all -- "dotnet test" happily reports exit code 0 (it just
///    built the DLL) but never actually invokes VSTest, so no .trx is ever produced. This was
///    discovered against a real project (packages.config, NUnit3TestAdapter) that silently produced
///    nothing. The fix is to do what Visual Studio itself does: restore via MSBuild
///    (/p:RestorePackagesConfig=true, which needs an explicit SolutionDir since packages.config's
///    "..\packages" convention is solution-relative, not project-relative), build with the real
///    MSBuild.exe (<see cref="MsBuildLocator"/> -- dotnet build's bundled MSBuild lacks the same
///    targets here too), then run the built DLL directly through vstest.console.exe
///    (<see cref="VsTestLocator"/>). Verified against a real 1000+ test classic NUnit project.
///  - An already-built assembly (<see cref="TestSuiteConfig.ProjectPath"/> is a <c>.dll</c>, not a
///    project file) skips build entirely and runs vstest directly against it -- this is what remote
///    (dev-server) execution uses: <see cref="Publisher"/> builds/publishes each configured suite at
///    publish time and uploads the result as a sibling "test bundle" artifact, so the server never
///    needs the project's source, git, or (for a modern suite) even Visual Studio -- just the .NET
///    SDK's own built-in <c>dotnet vstest</c>. A classic assembly still needs the desktop
///    vstest.console.exe (dotnet's test host can't load a non-.NET-Core assembly), detected the same
///    way as the "build from source" classic path above.</summary>
public sealed class DotNetUnitTestRunner : IUnitTestRunner
{
    public IReadOnlyList<TestSuiteConfig> GetConfiguredSuites(ProjectConfig project) =>
        project.TestSuites.Where(s => !string.IsNullOrWhiteSpace(s.ProjectPath)).ToList();

    public async Task<UnitTestRunResult> RunSuiteAsync(ProjectConfig project, TestSuiteConfig suite, UnitTestContext context, CancellationToken ct)
    {
        var testProjectPath = suite.ProjectPath!;
        if (!File.Exists(testProjectPath))
        {
            throw new InvalidOperationException($"'{project.Name}' test suite '{suite.Name}' not found at '{testProjectPath}'.");
        }

        Directory.CreateDirectory(context.WorkingDir);

        var (trxPath, exitCode) = Path.GetExtension(testProjectPath).Equals(".dll", StringComparison.OrdinalIgnoreCase)
            ? await RunViaPrebuiltAssemblyAsync(suite, testProjectPath, context, ct)
            : IsSdkStyleProject(testProjectPath)
                ? await RunViaDotNetTestAsync(project, suite, testProjectPath, context, ct)
                : await RunViaClassicMsBuildAndVsTestAsync(project, suite, testProjectPath, context, ct);

        var cases = TrxTestResultParser.Parse(trxPath);
        return new UnitTestRunResult(exitCode == 0, cases);
    }

    internal static bool IsSdkStyleProject(string testProjectPath) =>
        XDocument.Load(testProjectPath).Root?.Attribute("Sdk") is not null;

    private static async Task<(string TrxPath, int ExitCode)> RunViaDotNetTestAsync(
        ProjectConfig project, TestSuiteConfig suite, string testProjectPath, UnitTestContext context, CancellationToken ct)
    {
        context.Output.Stage($"Running dotnet test ({suite.Name})...");
        var exitCode = await ProcessRunner.RunAsync(
            "dotnet",
            $"test \"{testProjectPath}\" --logger \"trx;LogFileName=results.trx\" --results-directory \"{context.WorkingDir}\"",
            context.Output, treatStderrAsError: true, context.WorkingDir, context.EnvironmentVariables, ct);

        var trxPath = Path.Combine(context.WorkingDir, "results.trx");
        if (!File.Exists(trxPath))
        {
            throw new InvalidOperationException(
                $"dotnet test ({suite.Name}) exited with code {exitCode} but produced no results.trx -- see log output above for details.");
        }

        return (trxPath, exitCode);
    }

    // Classic .NET Framework test projects need restore + build via the real MSBuild.exe, then
    // vstest.console.exe run directly against the built assembly -- "dotnet test" never invokes
    // VSTest for these (see the class remarks above).
    private static async Task<(string TrxPath, int ExitCode)> RunViaClassicMsBuildAndVsTestAsync(
        ProjectConfig project, TestSuiteConfig suite, string testProjectPath, UnitTestContext context, CancellationToken ct)
    {
        var msBuildExePath = await MsBuildLocator.LocateAsync(context.MsBuildPath, ct);
        await BuildClassicProjectAsync(suite.Name, testProjectPath, msBuildExePath, context.Output, ct);

        var testProjectDir = Path.GetDirectoryName(testProjectPath)!;
        var testAssemblyPath = ResolveAssemblyPath(testProjectPath, Path.Combine(testProjectDir, "bin"));
        if (testAssemblyPath is null)
        {
            throw new InvalidOperationException($"Built {suite.Name}, but couldn't find its output assembly under '{testProjectDir}\\bin'.");
        }

        return await RunClassicVsTestAsync(project.Name, suite.Name, testAssemblyPath, context.WorkingDir, context.Output, context.EnvironmentVariables, ct);
    }

    // The remote-execution path: TestSuiteConfig.ProjectPath already points at a built assembly
    // (Publisher published/built it at publish time and uploaded the result as a sibling "test
    // bundle" artifact) -- no restore/build/git/source needed at all here, just run it. A modern
    // (SDK-style-produced) assembly always has a sibling .runtimeconfig.json written by the SDK
    // build; a classic .NET Framework assembly never does -- that's a reliable, cheap way to tell
    // them apart without needing to know anything about how the assembly was originally built.
    private static async Task<(string TrxPath, int ExitCode)> RunViaPrebuiltAssemblyAsync(
        TestSuiteConfig suite, string assemblyPath, UnitTestContext context, CancellationToken ct)
    {
        var isModern = File.Exists(Path.ChangeExtension(assemblyPath, ".runtimeconfig.json"));
        if (isModern)
        {
            await EnsurePlaywrightBrowsersInstalledAsync(suite.Name, assemblyPath, context.Output, ct);

            context.Output.Stage($"Running dotnet vstest ({suite.Name})...");
            var exitCode = await ProcessRunner.RunAsync(
                "dotnet",
                $"vstest \"{assemblyPath}\" /logger:\"trx;LogFileName=results.trx\" /ResultsDirectory:\"{context.WorkingDir}\"",
                context.Output, treatStderrAsError: false, workingDirectory: null, context.EnvironmentVariables, ct);

            var trxPath = Path.Combine(context.WorkingDir, "results.trx");
            if (!File.Exists(trxPath))
            {
                throw new InvalidOperationException(
                    $"dotnet vstest ({suite.Name}) exited with code {exitCode} but produced no results.trx -- see log output above for details.");
            }

            return (trxPath, exitCode);
        }

        return await RunClassicVsTestAsync(projectName: null, suite.Name, assemblyPath, context.WorkingDir, context.Output, context.EnvironmentVariables, ct);
    }

    // A Playwright-based suite's own build output includes a "playwright.ps1" script (generated
    // automatically by the Microsoft.Playwright package) that downloads the actual browser binaries
    // into a shared, machine-wide cache -- since the remote test-bundle copies a suite's ENTIRE build
    // output, this script is already sitting right next to the assembly after extraction, with no
    // extra bundling work needed. Running it here (idempotent -- a fast no-op once browsers are
    // already cached) means a dev server never needs anyone to manually run "playwright install"
    // themselves; the first remote run of a Playwright suite installs it automatically, and every
    // run after that reuses the same cache. A non-Playwright suite's output simply has no such
    // script, so this is a silent no-op for it.
    private static async Task EnsurePlaywrightBrowsersInstalledAsync(string suiteName, string assemblyPath, IOutputSink output, CancellationToken ct)
    {
        var scriptPath = Path.Combine(Path.GetDirectoryName(assemblyPath)!, "playwright.ps1");
        if (!File.Exists(scriptPath))
        {
            return;
        }

        output.Stage($"Ensuring Playwright browsers are installed ({suiteName})...");
        var exitCode = await ProcessRunner.RunAsync(
            "powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" install",
            output, treatStderrAsError: false, ct);
        if (exitCode != 0)
        {
            output.Warn($"{suiteName}: Playwright browser install exited with code {exitCode} -- the test run below may still fail if browsers are missing.");
        }
    }

    // Shared by the classic "build from source" path and the classic half of the prebuilt-assembly
    // path -- both end up needing the exact same "locate vstest.console.exe, run it against one
    // assembly, confirm a .trx came out" sequence.
    private static async Task<(string TrxPath, int ExitCode)> RunClassicVsTestAsync(
        string? projectName, string suiteName, string assemblyPath, string workingDir, IOutputSink output,
        IReadOnlyDictionary<string, string>? environmentVariables, CancellationToken ct)
    {
        var vsTestExePath = await VsTestLocator.LocateAsync(ct);
        if (vsTestExePath is null)
        {
            var context = projectName is null ? string.Empty : $"'{projectName}' ";
            throw new InvalidOperationException(
                $"{context}test suite '{suiteName}' is a classic (.NET Framework) test assembly, but vstest.console.exe " +
                "couldn't be located -- install Visual Studio (or Build Tools) with a testing workload.");
        }

        output.Stage($"Running vstest ({suiteName})...");
        var exitCode = await ProcessRunner.RunAsync(
            vsTestExePath,
            $"\"{assemblyPath}\" /logger:\"trx;LogFileName=results.trx\" /ResultsDirectory:\"{workingDir}\"",
            output, treatStderrAsError: false, workingDirectory: null, environmentVariables, ct);

        var trxPath = Path.Combine(workingDir, "results.trx");
        if (!File.Exists(trxPath))
        {
            throw new InvalidOperationException(
                $"vstest ({suiteName}) exited with code {exitCode} but produced no results.trx -- see log output above for details.");
        }

        return (trxPath, exitCode);
    }

    /// <summary>Restores (with an auto-detected SolutionDir) and builds a classic test project via
    /// the real MSBuild.exe -- just the "produce a built assembly" half of
    /// <see cref="RunViaClassicMsBuildAndVsTestAsync"/>, reused as-is by
    /// <see cref="Publisher"/>'s remote test-bundle step (which needs the build output copied
    /// somewhere for zipping, not run immediately).</summary>
    internal static async Task BuildClassicProjectAsync(string suiteName, string testProjectPath, string msBuildExePath, IOutputSink output, CancellationToken ct)
    {
        var testProjectDir = Path.GetDirectoryName(testProjectPath)!;
        var solutionDir = FindSolutionDir(testProjectDir);

        output.Stage($"Restoring packages for {suiteName}...");
        var restoreExitCode = await ProcessRunner.RunAsync(
            msBuildExePath,
            $"\"{testProjectPath}\" /t:Restore /p:RestorePackagesConfig=true /p:SolutionDir=\"{solutionDir}\" /nologo",
            output, treatStderrAsError: true, ct);
        if (restoreExitCode != 0)
        {
            throw new InvalidOperationException($"Restoring {suiteName} exited with code {restoreExitCode} -- see log output above for details.");
        }

        output.Stage($"Building {suiteName}...");
        var buildExitCode = await ProcessRunner.RunAsync(
            msBuildExePath,
            $"\"{testProjectPath}\" /p:Configuration=Debug /p:SolutionDir=\"{solutionDir}\" /nologo",
            output, treatStderrAsError: true, ct);
        if (buildExitCode != 0)
        {
            throw new InvalidOperationException($"Building {suiteName} exited with code {buildExitCode} -- see log output above for details.");
        }
    }

    /// <summary>Finds a project's built assembly (by its <c>&lt;AssemblyName&gt;</c>, or the project
    /// file's own base name if unset) under <paramref name="searchDir"/> -- the newest match by write
    /// time, in case stale output from another configuration is also present. Used both for the
    /// classic local-run path (searching the project's own <c>bin\</c>) and by <see cref="Publisher"/>'s
    /// remote test-bundle step (searching wherever it just built/published to).</summary>
    internal static string? ResolveAssemblyPath(string testProjectPath, string searchDir)
    {
        var assemblyName = XDocument.Load(testProjectPath).Descendants("AssemblyName").FirstOrDefault()?.Value
            ?? Path.GetFileNameWithoutExtension(testProjectPath);

        return Directory.Exists(searchDir)
            ? Directory.EnumerateFiles(searchDir, $"{assemblyName}.dll", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
    }

    // packages.config's "..\packages\..." HintPaths are resolved relative to the project file, but
    // MSBuild's own package restore for packages.config projects needs an explicit SolutionDir
    // property (it has no project file to infer one from, unlike PackageReference restore) -- walk
    // up looking for the real .sln so multi-level repo layouts don't silently restore to the wrong
    // packages folder. Falls back to the project's own parent directory (matching the "packages sits
    // beside the .sln, one level up from most classic test project folders" convention) if no .sln
    // is found within a reasonable number of levels.
    private static string FindSolutionDir(string startDir)
    {
        // Trailing separator is a forward slash, not '\', even though this is Windows-only --
        // this value gets interpolated into a quoted "/p:SolutionDir=..." command-line argument
        // below, and a backslash immediately before the closing " is parsed as an escaped literal
        // quote rather than the string terminator, corrupting the entire command line (see the
        // near-identical trap already documented on MsBuildRunner.BuildSdkStyleArgs). MSBuild/NuGet
        // both accept '/' in Windows paths without issue.
        var dir = new DirectoryInfo(startDir);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            if (dir.EnumerateFiles("*.sln").Any())
            {
                return dir.FullName + "/";
            }
        }

        return (Directory.GetParent(startDir)?.FullName ?? startDir) + "/";
    }
}
