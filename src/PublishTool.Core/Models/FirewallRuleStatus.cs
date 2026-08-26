namespace PublishTool.Core.Models;

/// <summary>One inbound Windows Firewall rule that PublishTool created (see
/// <see cref="Services.FirewallManager"/>) -- never a rule PublishTool didn't create itself.</summary>
public sealed class FirewallRuleStatus
{
    public required string Name { get; set; }

    /// <summary>"TCP" or "UDP".</summary>
    public required string Protocol { get; set; }

    /// <summary>As reported by netsh -- usually a single port number, occasionally a range.</summary>
    public required string Port { get; set; }

    public required bool Enabled { get; set; }
}
