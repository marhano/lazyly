using PublishTool.Core.Models;

namespace PublishTool.Core.Services;

/// <summary>Checks for the external tools PublishTool shells out to (MSBuild, IIS's appcmd, and --
/// for Angular/Android projects -- Node/npm, a JDK, and the Android SDK), so missing-dependency
/// failures can be surfaced up front instead of only at publish time. Node/npm, Java, and the
/// Android SDK are only actually needed if you have an Angular or Android project registered --
/// they're still checked unconditionally here (cheap, and this list has no per-project context),
/// same as IIS already was for projects that don't use it.</summary>
public static class DependencyChecker
{
    public static async Task<IReadOnlyList<DependencyCheckResult>> CheckAllAsync(
        string? configuredMsBuildPath, CancellationToken ct = default)
    {
        var results = new List<DependencyCheckResult>
        {
            await CheckMsBuildAsync(configuredMsBuildPath, ct),
            CheckIis(),
            await CheckOnPathAsync(
                "Node.js / npm", "node",
                "Not found on PATH -- required to build Angular and Android (Capacitor/Cordova) projects. Install Node.js from nodejs.org.",
                ct),
            await CheckOnPathAsync(
                "Java (JDK)", "java",
                "Not found on PATH -- required to run an Android project's own Gradle wrapper (gradlew). Install a JDK and make sure 'java' is on PATH.",
                ct),
            CheckAndroidSdk(),
        };

        return results;
    }

    private static async Task<DependencyCheckResult> CheckMsBuildAsync(string? configuredMsBuildPath, CancellationToken ct)
    {
        try
        {
            var path = await MsBuildLocator.LocateAsync(configuredMsBuildPath, ct);
            return new DependencyCheckResult { Name = "MSBuild", IsAvailable = true, Details = path };
        }
        catch (Exception ex)
        {
            return new DependencyCheckResult { Name = "MSBuild", IsAvailable = false, Details = ex.Message };
        }
    }

    private static DependencyCheckResult CheckIis()
    {
        var appCmdPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "inetsrv", "appcmd.exe");
        var exists = File.Exists(appCmdPath);

        return new DependencyCheckResult
        {
            Name = "IIS (appcmd.exe)",
            IsAvailable = exists,
            Details = exists ? appCmdPath : "Not found -- IIS doesn't appear to be installed on this machine.",
        };
    }

    /// <summary>Resolves <paramref name="command"/> via <c>where</c> (through cmd.exe, same reasoning
    /// as <see cref="BuildRunners.ShellCommandRunner"/> -- npm/node/java shims aren't always directly
    /// startable the way MSBuild/appcmd's full .exe paths are) rather than actually invoking the tool
    /// with a version flag, since e.g. <c>java -version</c> writes to stderr on success, which would
    /// otherwise need its own special-casing here for no real benefit.</summary>
    private static async Task<DependencyCheckResult> CheckOnPathAsync(
        string displayName, string command, string missingMessage, CancellationToken ct)
    {
        try
        {
            var (exitCode, output) = await ProcessRunner.RunCapturedAsync("cmd.exe", $"/c where {command}", ct);
            if (exitCode == 0)
            {
                var resolvedPath = output
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .FirstOrDefault(line => line.Length > 0) ?? command;
                return new DependencyCheckResult { Name = displayName, IsAvailable = true, Details = resolvedPath };
            }

            return new DependencyCheckResult { Name = displayName, IsAvailable = false, Details = missingMessage };
        }
        catch (Exception ex)
        {
            return new DependencyCheckResult { Name = displayName, IsAvailable = false, Details = ex.Message };
        }
    }

    /// <summary>The Android SDK has no single canonical .exe to resolve on PATH the way Node/Java
    /// do -- Android Studio and the Gradle plugin both locate it via one of these two environment
    /// variables instead, so that's what's actually checked here.</summary>
    private static DependencyCheckResult CheckAndroidSdk()
    {
        var sdkRoot = Environment.GetEnvironmentVariable("ANDROID_HOME") ?? Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT");
        var exists = !string.IsNullOrWhiteSpace(sdkRoot) && Directory.Exists(sdkRoot);

        return new DependencyCheckResult
        {
            Name = "Android SDK",
            IsAvailable = exists,
            Details = exists
                ? sdkRoot!
                : "ANDROID_HOME/ANDROID_SDK_ROOT isn't set (or doesn't point to a real folder) -- required to build Android " +
                  "(Capacitor/Cordova) projects. Install Android Studio's SDK and set one of those environment variables.",
        };
    }
}
