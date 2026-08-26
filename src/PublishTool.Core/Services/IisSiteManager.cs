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
    private readonly IisAuditStore _auditStore;
    private readonly string _auditRoot;

    public IisSiteManager(IOutputSink output, string? deploymentsRoot = null, string? auditRoot = null)
    {
        _output = output;
        _deploymentStore = new SiteDeploymentStore();
        _deploymentsRoot = deploymentsRoot ?? SiteDeploymentStore.DefaultRoot;
        _auditStore = new IisAuditStore();
        _auditRoot = auditRoot ?? IisAuditStore.DefaultRoot;
    }

    private static string AppCmdPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "inetsrv", "appcmd.exe");

    public async Task EnsureSiteExistsAsync(
        string siteName, string physicalPath, IReadOnlyList<IisBinding> bindings,
        AppPoolRuntimeTemplate poolTemplate = AppPoolRuntimeTemplate.DotNetFramework, CancellationToken ct = default)
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
        // may not suit the app at all, which is what shows as "Unknown" status in IIS Manager --
        // the pool exists but can't actually run the app. So: give every auto-created site its
        // own pool, sized for whichever runtime template fits this project (see AppPoolRuntimeTemplate).
        await EnsureAppPoolExistsAsync(siteName, poolTemplate, ct);

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

    private async Task EnsureAppPoolExistsAsync(string poolName, AppPoolRuntimeTemplate poolTemplate, CancellationToken ct)
    {
        if (await AppPoolExistsAsync(poolName, ct))
        {
            _output.Info($"Application pool '{poolName}' already exists; reusing it.");
            return;
        }

        // "" (quoted, empty) is appcmd's syntax for "No Managed Code" -- IIS's CLR hosting isn't
        // used either way for a static-file site or an app that runs its own runtime (Kestrel/ANCM).
        var (runtimeVersion, description) = poolTemplate == AppPoolRuntimeTemplate.NoManagedCode
            ? ("", "No Managed Code")
            : ("v4.0", ".NET CLR v4.0");

        _output.Info($"Creating application pool '{poolName}' ({description}, Integrated pipeline)...");
        var args = $"add apppool /name:\"{poolName}\" /managedRuntimeVersion:\"{runtimeVersion}\" /managedPipelineMode:Integrated";

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

    /// <param name="performedBy">Who to record this in the audit trail as -- see
    /// <see cref="GetAuditHistoryAsync"/>. Null (the default) means "don't audit this call at all",
    /// used by <see cref="BuildDeployer"/>'s automatic pre/post-deploy pool bounce, which already has
    /// its own audit trail (deployment history, the Projects tab's "Deployed" entry) and would just
    /// double-log every single deploy here otherwise -- only an explicit user/CLI-initiated action
    /// should show up in this trail.</param>
    public Task StartSiteAsync(string siteName, string? performedBy = null, CancellationToken ct = default) =>
        RunManagementCommand(
            $"start site /site.name:\"{siteName}\"", $"start site '{siteName}'", "Site", siteName, "Started", performedBy, ct);

    public Task StopSiteAsync(string siteName, string? performedBy = null, CancellationToken ct = default) =>
        RunManagementCommand(
            $"stop site /site.name:\"{siteName}\"", $"stop site '{siteName}'", "Site", siteName, "Stopped", performedBy, ct);

    public Task StartAppPoolAsync(string poolName, string? performedBy = null, CancellationToken ct = default) =>
        RunManagementCommand(
            $"start apppool /apppool.name:\"{poolName}\"", $"start application pool '{poolName}'", "AppPool", poolName, "Started", performedBy, ct);

    public Task StopAppPoolAsync(string poolName, string? performedBy = null, CancellationToken ct = default) =>
        RunManagementCommand(
            $"stop apppool /apppool.name:\"{poolName}\"", $"stop application pool '{poolName}'", "AppPool", poolName, "Stopped", performedBy, ct);

    public Task RecycleAppPoolAsync(string poolName, string? performedBy = null, CancellationToken ct = default) =>
        RunManagementCommand(
            $"recycle apppool /apppool.name:\"{poolName}\"", $"recycle application pool '{poolName}'", "AppPool", poolName, "Recycled", performedBy, ct);

    /// <summary>Deletes the site and, best-effort, the app pool PublishTool would have given it if
    /// it auto-created it (see <see cref="EnsureAppPoolExistsAsync"/> -- always named exactly like
    /// the site). Silently leaves the pool alone if none exists by that name, or if it's still
    /// serving another site (appcmd's own delete just fails in that case) -- this is a convenience
    /// for the common "PublishTool made this site and its dedicated pool, remove both together"
    /// case, not a general-purpose "find whatever pool this site actually uses" resolver.</summary>
    public async Task DeleteSiteAsync(string siteName, string? performedBy = null, CancellationToken ct = default)
    {
        await RunManagementCommand(
            $"delete site /site.name:\"{siteName}\"", $"delete site '{siteName}'", "Site", siteName, "Removed", performedBy, ct);

        if (await AppPoolExistsAsync(siteName, ct))
        {
            var exitCode = await ProcessRunner.RunAsync(AppCmdPath, $"delete apppool /apppool.name:\"{siteName}\"", _output, ct);
            if (exitCode == 0)
            {
                _output.Info($"Also removed application pool '{siteName}'.");
            }
            else
            {
                _output.Warn($"Site '{siteName}' was removed, but its application pool couldn't be removed too " +
                              "(it may still be in use by another site) -- remove it manually if it's no longer needed.");
            }
        }
    }

    /// <summary>Full Start/Stop/Removed/Recycled audit trail (newest-first) for explicit
    /// user/CLI-initiated IIS actions -- see the "performedBy" remarks on each action above for
    /// what's deliberately excluded.</summary>
    public Task<IReadOnlyList<IisAuditEntry>> GetAuditHistoryAsync(CancellationToken ct = default) =>
        _auditStore.GetHistoryAsync(_auditRoot, ct);

    private async Task RunManagementCommand(
        string args, string actionDescription, string entityType, string entityName, string auditAction, string? performedBy, CancellationToken ct)
    {
        RequireAppCmd();

        var exitCode = await ProcessRunner.RunAsync(AppCmdPath, args, _output, ct);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to {actionDescription} (appcmd exited with code {exitCode}). Try running PublishTool as Administrator.");
        }

        _output.Info($"Done: {actionDescription}.");

        if (performedBy is not null)
        {
            await TryRecordAuditAsync(new IisAuditEntry
            {
                EntityType = entityType,
                EntityName = entityName,
                Action = auditAction,
                PerformedAtUtc = DateTimeOffset.UtcNow,
                PerformedBy = performedBy,
            }, ct);
        }
    }

    /// <summary>Best-effort -- a missing/unwritable audit log is a diagnostic nicety, not
    /// something that should fail an otherwise-successful IIS action.</summary>
    private async Task TryRecordAuditAsync(IisAuditEntry entry, CancellationToken ct)
    {
        try
        {
            await _auditStore.AppendAsync(_auditRoot, entry, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _output.Warn($"{entry.Action} succeeded, but couldn't record it in the IIS audit trail: {ex.Message}");
        }
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
