using System.Diagnostics;

namespace PublishTool.Core.Services;

internal static class ProcessRunner
{
    public static async Task<int> RunAsync(
        string fileName,
        string arguments,
        IOutputSink output,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.Info(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.Error(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(ct);

        return process.ExitCode;
    }
}
