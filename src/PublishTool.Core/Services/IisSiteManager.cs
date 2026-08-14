using System.Globalization;
using System.Text.RegularExpressions;
using PublishTool.Core.Models;

namespace PublishTool.Core.Services;

/// <summary>
/// Creates, lists, and manages IIS sites/application pools via appcmd.exe rather than
/// referencing Microsoft.Web.Administration.dll directly -- that assembly is a .NET Framework
/// component shipped with IIS, and its compatibility when loaded from a modern .NET
/// (non-Framework) app is unreliable. appcmd is the officially supported CLI for the same
/// operations and works identically regardless of the caller's runtime, matching the same
/// shell-out pattern already used for msbuild/robocopy. EnsureSiteExistsAsync never modifies
/// an existing site -- only creates one if none exists with the given name.
/// </summary>
public sealed partial class IisSiteManager
{
    private readonly IOutputSink _output;
    private readonly SiteDeploymentStore _deploymentStore;
    private readonly string _deploymentsRoot;

    public IisSiteManager(IOutputSink output, string? deploymentsRoot = null)
    {
        _output = output;
        _deploymentStore = new SiteDeploymentStore();
        _deploymentsRoot = deploymentsRoot ?? SiteDeploymentStore.DefaultRoot;
    }

    private static string AppCmdPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "inetsrv", "appcmd.exe");

    public async Task EnsureSiteExistsAsync(
        string siteName, string physicalPath, IReadOnlyList<IisBinding> bindings, CancellationToken ct = default)
    {
        RequireAppCmd();

        if (await SiteExistsAsync(siteName, ct))
        {
            _output.Info($"IIS site '{siteName}' already exists; leaving it as-is.");
            return;
        }

        if (bindings.Count == 0)
        {
            throw new InvalidOperationException(
                $"Auto-create IIS site is enabled for '{siteName}' but no bindings were configured.");
        }

        _output.Info($"IIS site '{siteName}' not found; creating it...");

        // appcmd's "add site" points the root application at DefaultAppPool unless told
        // otherwise -- unlike IIS Manager's own "Add Website" wizard, which always creates a
        // dedicated pool matching the site name. Sharing DefaultAppPool is bad practice (no
        // crash isolation between sites) and, worse, DefaultAppPool's managed runtime version
        // may not suit a classic .NET Framework app at all, which is what shows as "Unknown"
        // status in IIS Manager -- the pool exists but can't actually run the app. So: give
        // every auto-created site its own pool, sized for classic .NET Framework 4.x (all 4.x
        // versions share CLR v4.0).
        await EnsureAppPoolExistsAsync(siteName, ct);

        var bindingArg = string.Join(",", bindings.Select(FormatBinding));
        var addSiteArgs = $"add site /name:\"{siteName}\" /physicalPath:\"{physicalPath}\" /bindings:\"{bindingArg}\"";

        var exitCode = await ProcessRunner.RunAsync(AppCmdPath, addSiteArgs, _output, ct);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to create IIS site '{siteName}' (appcmd exited with code {exitCode}). " +
                "Try running PublishTool as Administrator.");
        }

        var assignPoolArgs = $"set app \"{siteName}/\" /applicationPool:\"{siteName}\"";
        var assignExitCode = await ProcessRunner.RunAsync(AppCmdPath, assignPoolArgs, _output, ct);
        if (assignExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Site '{siteName}' was created, but assigning it to its own application pool failed " +
                $"(appcmd exited with code {assignExitCode}). The site exists but likely still shows as broken.");
        }

        _output.Info($"IIS site '{siteName}' created with its own application pool.");
    }

    private async Task EnsureAppPoolExistsAsync(string poolName, CancellationToken ct)
    {
        if (await AppPoolExistsAsync(poolName, ct))
        {
            _output.Info($"Application pool '{poolName}' already exists; reusing it.");
            return;
        }

        _output.Info($"Creating application pool '{poolName}' (.NET CLR v4.0, Integrated pipeline)...");
        var args = $"add apppool /name:\"{poolName}\" /managedRuntimeVersion:v4.0 /managedPipelineMode:Integrated";

        var exitCode = await ProcessRunner.RunAsync(AppCmdPath, args, _output, ct);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to create application pool '{poolName}' (appcmd exited with code {exitCode}).");
        }
    }

    private static async Task<bool> SiteExistsAsync(string siteName, CancellationToken ct)
    {
        var exitCode = await ProcessRunner.RunSilentAsync(AppCmdPath, $"list site /name:\"{siteName}\"", ct);
        return exitCode == 0;
    }

    /// <summary>True if an application pool with this exact name exists. Also false (rather than
    /// throwing) if IIS itself isn't installed on this machine -- callers checking "is this
    /// project even IIS-hosted" want that treated the same as "no such pool".</summary>
    public static async Task<bool> AppPoolExistsAsync(string poolName, CancellationToken ct = default)
    {
        if (!File.Exists(AppCmdPath))
        {
            return false;
        }

        var exitCode = await ProcessRunner.RunSilentAsync(AppCmdPath, $"list apppool /name:\"{poolName}\"", ct);
        return exitCode == 0;
    }

    private static string FormatBinding(IisBinding binding)
    {
        var ip = string.IsNullOrWhiteSpace(binding.IpAddress) ? "*" : binding.IpAddress;
        var host = binding.HostName ?? string.Empty;
        return $"{binding.Protocol}/{ip}:{binding.Port.ToString(CultureInfo.InvariantCulture)}:{host}";
    }

    public async Task<IReadOnlyList<IisSiteStatus>> ListSitesAsync(CancellationToken ct = default)
    {
        RequireAppCmd();

        var (exitCode, output) = await ProcessRunner.RunCapturedAsync(AppCmdPath, "list site", ct);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to list IIS sites (appcmd exited with code {exitCode}). Try running PublishTool as Administrator.");
        }

        var results = new List<IisSiteStatus>();
        foreach (var line in output.Split('\n'))
        {
            var match = SiteLineRegex().Match(line.Trim());
            if (!match.Success)
            {
                continue;
            }

            var status = new IisSiteStatus
            {
                Name = match.Groups["name"].Value,
                Bindings = match.Groups["bindings"].Value,
                State = match.Groups["state"].Value,
            };
            await TryEnrichWithDeploymentInfoAsync(status, ct);
            results.Add(status);
        }

        return results;
    }

    /// <summary>Best-effort -- a site PublishTool never deployed to (e.g. Default Web Site) simply
    /// has no history, and any read failure here shouldn't break the whole site listing.</summary>
    private async Task TryEnrichWithDeploymentInfoAsync(IisSiteStatus status, CancellationToken ct)
    {
        try
        {
            var history = await _deploymentStore.GetHistoryAsync(_deploymentsRoot, status.Name, ct);
            var latest = history.FirstOrDefault();
            if (latest is null)
            {
                return;
            }

            status.DeployedVersion = latest.Version;
            status.DeployedAtUtc = latest.DeployedAtUtc;
            status.DeployedBy = latest.DeployedBy;
            status.DeployedEnvironment = latest.EnvironmentName;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _output.Warn($"Couldn't read deployment history for '{status.Name}': {ex.Message}");
        }
    }

    /// <summary>Full deployment history for one site, newest-first -- for the IIS tab's History
    /// dialog. Not enriched onto <see cref="IisSiteStatus"/> itself since that's only ever the
    /// latest deploy; this is the separate, on-demand full list.</summary>
    public Task<IReadOnlyList<SiteDeploymentRecord>> GetDeploymentHistoryAsync(string siteName, CancellationToken ct = default) =>
        _deploymentStore.GetHistoryAsync(_deploymentsRoot, siteName, ct);

    public async Task<IReadOnlyList<IisAppPoolStatus>> ListAppPoolsAsync(CancellationToken ct = default)
    {
        RequireAppCmd();

        var (exitCode, output) = await ProcessRunner.RunCapturedAsync(AppCmdPath, "list apppool", ct);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to list IIS application pools (appcmd exited with code {exitCode}). Try running PublishTool as Administrator.");
        }

        var results = new List<IisAppPoolStatus>();
        foreach (var line in output.Split('\n'))
        {
            var match = AppPoolLineRegex().Match(line.Trim());
            if (!match.Success)
            {
                continue;
            }

            results.Add(new IisAppPoolStatus
            {
                Name = match.Groups["name"].Value,
                ManagedRuntimeVersion = match.Groups["version"].Value,
                PipelineMode = match.Groups["mode"].Value,
                State = match.Groups["state"].Value,
            });
        }

        return results;
    }

    public Task StartSiteAsync(string siteName, CancellationToken ct = default) =>
        RunManagementCommand($"start site /site.name:\"{siteName}\"", $"start site '{siteName}'", ct);

    public Task StopSiteAsync(string siteName, CancellationToken ct = default) =>
        RunManagementCommand($"stop site /site.name:\"{siteName}\"", $"stop site '{siteName}'", ct);

    public Task StartAppPoolAsync(string poolName, CancellationToken ct = default) =>
        RunManagementCommand($"start apppool /apppool.name:\"{poolName}\"", $"start application pool '{poolName}'", ct);

    public Task StopAppPoolAsync(string poolName, CancellationToken ct = default) =>
        RunManagementCommand($"stop apppool /apppool.name:\"{poolName}\"", $"stop application pool '{poolName}'", ct);

    public Task RecycleAppPoolAsync(string poolName, CancellationToken ct = default) =>
        RunManagementCommand($"recycle apppool /apppool.name:\"{poolName}\"", $"recycle application pool '{poolName}'", ct);

    private async Task RunManagementCommand(string args, string actionDescription, CancellationToken ct)
    {
        RequireAppCmd();

        var exitCode = await ProcessRunner.RunAsync(AppCmdPath, args, _output, ct);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to {actionDescription} (appcmd exited with code {exitCode}). Try running PublishTool as Administrator.");
        }

        _output.Info($"Done: {actionDescription}.");
    }

    private static void RequireAppCmd()
    {
        if (!File.Exists(AppCmdPath))
        {
            throw new InvalidOperationException("IIS doesn't appear to be installed on this machine (appcmd.exe not found).");
        }
    }

    // appcmd's plain-text listing format has been stable since IIS 7:
    //   SITE "Default Web Site" (id:1,bindings:http/*:80:,state:Started)
    //   APPPOOL "DefaultAppPool" (MgdVersion:v4.0,MgdMode:Integrated,state:Started)
    // Bindings can themselves contain commas (multiple bindings per site), so the bindings
    // group is greedy up to the *last* ",state:" rather than stopping at the first comma.
    [GeneratedRegex("""^SITE\s+"(?<name>[^"]+)"\s+\(id:\d+,bindings:(?<bindings>.*),state:(?<state>\w+)\)$""")]
    private static partial Regex SiteLineRegex();

    [GeneratedRegex("""^APPPOOL\s+"(?<name>[^"]+)"\s+\(MgdVersion:(?<version>[^,]*),MgdMode:(?<mode>[^,]*),state:(?<state>\w+)\)$""")]
    private static partial Regex AppPoolLineRegex();
}
