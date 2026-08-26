namespace PublishTool.Core.Models;

/// <summary>
/// One entry in the IIS tab's audit trail (see <see cref="Services.IisAuditStore"/>) -- a single
/// global log covering explicit user-initiated site/app pool actions, same "one flat log, not one
/// file per site" reasoning as <see cref="FirewallAuditEntry"/>: a removed site still needs to stay
/// visible in history. Deliberately NOT recorded for the automatic app-pool stop/start
/// <see cref="Services.BuildDeployer"/> does around every deploy -- that's already covered by
/// <see cref="Services.SiteDeploymentStore"/> and the Projects tab's own audit trail, so double
/// logging it here would just be noise.
/// </summary>
public sealed class IisAuditEntry
{
    /// <summary>"Site" or "AppPool".</summary>
    public required string EntityType { get; set; }

    public required string EntityName { get; set; }

    /// <summary>"Started", "Stopped", "Removed" (sites), "Recycled" (pools), or "Manual Deploy".</summary>
    public required string Action { get; set; }

    public string? Details { get; set; }

    public required DateTimeOffset PerformedAtUtc { get; set; }

    public required string PerformedBy { get; set; }
}
