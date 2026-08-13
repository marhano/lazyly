using PublishTool.Core.Models;

namespace PublishTool.Core.Services;

/// <summary>
/// Makes an already-built package live in IIS: ensures the site exists (if requested), stops the
/// app pool (avoids the sharing violation robocopy would otherwise hit -- a running in-process app
/// pool holds its DLLs open), mirrors the files in, restarts the pool. Shared by three callers --
/// <see cref="Publisher"/>'s own local deploy step, the GUI's manual "deploy this version" action
/// on the Projects tab, and the Remote Build Hosting API's deploy endpoint -- one implementation
/// instead of three copies of the same sequence.
/// </summary>
public sealed class BuildDeployer
{
    private readonly IOutputSink _output;
    private readonly IisSiteManager _iisSiteManager;
    private readonly RobocopyMirror _mirror;

    public BuildDeployer(IOutputSink output)
    {
        _output = output;
        _iisSiteManager = new IisSiteManager(output);
        _mirror = new RobocopyMirror(output);
    }

    /// <param name="sourceDir">An already-extracted directory (MSBuild output, or a build's zip
    /// already unpacked by the caller) -- this method only handles the IIS side, not unzipping.</param>
    public async Task DeployAsync(
        string siteName, string hostPath, IReadOnlyList<IisBinding> bindings, bool autoCreateSite,
        string sourceDir, CancellationToken ct = default)
    {
        if (autoCreateSite)
        {
            _output.Stage("Ensuring IIS site exists...");
            await _iisSiteManager.EnsureSiteExistsAsync(siteName, hostPath, bindings, ct);
        }

        _output.Stage($"Deploying to IIS host path: {hostPath}");

        // A running IIS app pool for this site holds its DLLs open (in-process hosting keeps them
        // loaded in w3wp.exe), which makes robocopy fail to overwrite them with a sharing violation
        // -- stopping the pool first (best-effort; most projects aren't IIS-hosted at all, so a
        // missing pool is the normal case, not a problem) avoids that entirely instead of relying
        // on robocopy's slow 30-second retry loop.
        var appPoolWasStopped = await TryStopAppPoolForDeployAsync(siteName, ct);
        try
        {
            await _mirror.MirrorAsync(sourceDir, hostPath, ct);
        }
        finally
        {
            if (appPoolWasStopped)
            {
                await TryStartAppPoolAsync(siteName, ct);
            }
        }
    }

    /// <summary>Stops the IIS application pool sharing this project's name, if one exists, so its
    /// files can be overwritten. Swallows any failure silently (IIS not installed, no such pool,
    /// no permission) -- most projects aren't IIS-hosted at all, so that's the expected case, not
    /// something worth warning about on every deploy.</summary>
    private async Task<bool> TryStopAppPoolForDeployAsync(string poolName, CancellationToken ct)
    {
        try
        {
            if (!await IisSiteManager.AppPoolExistsAsync(poolName, ct))
            {
                return false;
            }

            _output.Info($"Stopping IIS application pool '{poolName}' before copying files...");
            await _iisSiteManager.StopAppPoolAsync(poolName, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Restarts a pool this same deploy stopped. Unlike the stop side, a failure here is
    /// worth surfacing -- it means the site was left down.</summary>
    private async Task TryStartAppPoolAsync(string poolName, CancellationToken ct)
    {
        try
        {
            await _iisSiteManager.StartAppPoolAsync(poolName, ct);
            _output.Info($"Restarted IIS application pool '{poolName}'.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _output.Warn($"Couldn't restart IIS application pool '{poolName}' after deploying -- start it manually. ({ex.Message})");
        }
    }
}
