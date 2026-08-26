using System.Diagnostics;

namespace PublishTool.Core.Services;

internal static class ProcessRunner
{
    public static async Task<int> RunAsync(
        string fileName,
        string arguments,
        IOutputSink output,
        CancellationToken ct = default) =>
        await RunAsync(fileName, arguments, output, treatStderrAsError: true, workingDirectory: null, ct);

    /// <summary>
    /// Same as the other overload, but with control over whether stderr lines are logged as
    /// errors. Most tools here (MSBuild, appcmd) use stderr to mean "something's wrong", but git
    /// routinely writes ordinary status messages there too (e.g. "Switched to branch 'x'" on a
    /// perfectly successful checkout) -- callers driving git should pass false so a successful
    /// command doesn't show up looking like a failure in the output log.
    /// </summary>
    public static async Task<int> RunAsync(
        string fileName,
        string arguments,
        IOutputSink output,
        bool treatStderrAsError,
        CancellationToken ct = default) =>
        await RunAsync(fileName, arguments, output, treatStderrAsError, workingDirectory: null, ct);

    /// <summary>
    /// Same as the other overload, plus an explicit working directory -- for tools invoked by
    /// convention relative to a project root (npm, npx, gradlew) rather than by full path
    /// arguments the way MSBuild/git/appcmd are called elsewhere in this codebase.
    /// </summary>
    public static async Task<int> RunAsync(
        string fileName,
        string arguments,
        IOutputSink output,
        bool treatStderrAsError,
        string? workingDirectory,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            psi.WorkingDirectory = workingDirectory;
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.Info(e.Data); };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            if (treatStderrAsError)
            {
                output.Error(e.Data);
            }
            else
            {
                output.Info(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(ct);

        return process.ExitCode;
    }

    /// <summary>Runs a process without streaming its output anywhere -- for probes/checks
    /// whose expected-failure output (e.g. "site not found") would otherwise read as an error.</summary>
    public static async Task<int> RunSilentAsync(string fileName, string arguments, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)!;
        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }

    /// <summary>Runs a process and returns its combined stdout (or stdout+stderr on failure) for
    /// parsing, instead of streaming line-by-line to an IOutputSink.</summary>
    public static async Task<(int ExitCode, string Output)> RunCapturedAsync(
        string fileName, string arguments, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (process.ExitCode, process.ExitCode == 0 ? stdout : stdout + stderr);
    }
}
