namespace PublishTool.Core.Services;

public sealed class MsBuildRunner
{
    private readonly IOutputSink _output;

    public MsBuildRunner(IOutputSink output)
    {
        _output = output;
    }

    public async Task PublishAsync(
        string msBuildExePath,
        string csprojPath,
        string pubxmlName,
        string stagingDir,
        string? extraTargets = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(stagingDir);

        var args = $"\"{csprojPath}\" " +
                   "/p:DeployOnBuild=true " +
                   $"/p:PublishProfile=\"{pubxmlName}\" " +
                   "/p:Configuration=Release " +
                   $"/p:PublishUrl=\"{stagingDir}\"";

        // Some third-party package .targets files (e.g. SQLite's native interop DLL copy)
        // gate hooking into the web publish pipeline on a VisualStudioVersion whitelist that
        // predates this toolset, so their target never runs during Publish even though it runs
        // fine during a plain Build. Forcing it explicitly alongside the default Build target
        // (which DeployOnBuild=true turns into build+publish) works around that without needing
        // to touch VisualStudioVersion itself, which breaks locating Microsoft.WebApplication.targets.
        if (!string.IsNullOrWhiteSpace(extraTargets))
        {
            args += $" /t:{extraTargets};Build";
        }

        var exitCode = await ProcessRunner.RunAsync(msBuildExePath, args, _output, ct);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"msbuild exited with code {exitCode}. See log output above for details.");
        }
    }
}
