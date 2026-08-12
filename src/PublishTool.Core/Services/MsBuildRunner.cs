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
        bool sdkStyleProject,
        string? extraTargets = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(stagingDir);

        var args = sdkStyleProject
            ? BuildSdkStyleArgs(csprojPath, pubxmlName, stagingDir, extraTargets)
            : BuildClassicArgs(csprojPath, pubxmlName, stagingDir, extraTargets);

        var exitCode = await ProcessRunner.RunAsync(msBuildExePath, args, _output, ct);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"msbuild exited with code {exitCode}. See log output above for details.");
        }
    }

    // Classic .NET Framework web projects (Web Deploy / Microsoft.WebApplication.targets):
    // DeployOnBuild=true hooks the publish pipeline into the default Build target, and PublishUrl
    // is the FileSystem publish provider's output location.
    private static string BuildClassicArgs(string csprojPath, string pubxmlName, string stagingDir, string? extraTargets)
    {
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

        return args;
    }

    // Modern SDK-style projects (e.g. ASP.NET Core, Microsoft.NET.Sdk.Web/Publish): publish via
    // an explicit Publish target and PublishDir. DeployOnBuild=true must NOT be set here -- combined
    // with an explicit /t:Publish on this project style it causes MSBuild error MSB4006 (circular
    // dependency: Publish <- _CopyAspNetCoreFilesToIntermediateOutputPath <- ... <- Publish),
    // since DeployOnBuild is a classic Web Deploy hook this pipeline doesn't expect alongside its
    // own explicit target invocation. Both PublishDir and PublishUrl are passed since which one is
    // actually honored is a pubxml/SDK-version detail; the unused one is simply ignored.
    private static string BuildSdkStyleArgs(string csprojPath, string pubxmlName, string stagingDir, string? extraTargets)
    {
        var targets = new List<string> { "Publish" };
        if (!string.IsNullOrWhiteSpace(extraTargets))
        {
            targets.Add(extraTargets);
        }

        // No trailing backslash before the closing quote on either path property: a backslash
        // immediately before a closing " is parsed by Windows' command-line argument rules as an
        // escaped literal quote rather than the string terminator, so the argument never actually
        // closes and swallows everything after it (this broke the very first real test here).
        return $"\"{csprojPath}\" " +
               $"/t:{string.Join(';', targets)} " +
               $"/p:PublishProfile=\"{pubxmlName}\" " +
               "/p:Configuration=Release " +
               $"/p:PublishDir=\"{stagingDir}\" " +
               $"/p:PublishUrl=\"{stagingDir}\"";
    }
}
