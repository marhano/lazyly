namespace PublishTool.Core.Models;

/// <summary>
/// One entry in the Firewall tab's audit trail (see <see cref="Services.FirewallAuditStore"/>) --
/// unlike <see cref="SiteDeploymentRecord"/>, this is a single global log rather than one per
/// rule, since a removed rule still needs to stay visible in history.
/// </summary>
public sealed class FirewallAuditEntry
{
    /// <summary>"Added", "Edited", or "Removed".</summary>
    public required string Action { get; set; }

    public required string RuleName { get; set; }

    public required string Protocol { get; set; }

    public required string Ports { get; set; }

    /// <summary>Set only when <see cref="Action"/> is "Edited" -- what the rule looked like
    /// before this change.</summary>
    public string? PreviousRuleName { get; set; }

    public string? PreviousProtocol { get; set; }

    public string? PreviousPorts { get; set; }

    public required DateTimeOffset PerformedAtUtc { get; set; }

    public required string PerformedBy { get; set; }
}
