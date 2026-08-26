namespace PublishTool.Core.Services.BuildRunners;

/// <summary>
/// Runs a command line through <c>cmd.exe /c</c> in a given working directory -- needed for
/// npm/npx/ionic/cordova/gradlew.bat, which are Windows batch-file shims that <see cref="ProcessRunner"/>'s
/// direct <c>Process.Start</c> can't resolve the way it resolves a real .exe on PATH (unlike
/// MSBuild/git/appcmd, which this codebase always invokes by their actual .exe path or name).
/// stderr is not treated as an error here -- like git (see <see cref="ProcessRunner"/>'s stderr
/// comment), npm/ng/gradle routinely write ordinary build progress and warnings to stderr.
/// </summary>
internal static class ShellCommandRunner
{
    public static Task<int> RunAsync(string commandLine, string workingDirectory, IOutputSink output, CancellationToken ct) =>
        ProcessRunner.RunAsync("cmd.exe", $"/c \"{commandLine}\"", output, treatStderrAsError: false, workingDirectory, ct);
}
