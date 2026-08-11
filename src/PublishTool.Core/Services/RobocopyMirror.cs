namespace PublishTool.Core.Services;

public sealed class RobocopyMirror
{
    private readonly IOutputSink _output;

    public RobocopyMirror(IOutputSink output)
    {
        _output = output;
    }

    public async Task MirrorAsync(string sourceDir, string destDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destDir);

        var args = $"\"{sourceDir}\" \"{destDir}\" /MIR /NFL /NDL /NJH /NJS /NP";
        var exitCode = await ProcessRunner.RunAsync("robocopy", args, _output, ct);

        // Robocopy uses a bitmask where 0-7 are success/informational; 8+ means failure.
        if (exitCode >= 8)
        {
            throw new InvalidOperationException($"robocopy failed with exit code {exitCode}.");
        }
    }
}
