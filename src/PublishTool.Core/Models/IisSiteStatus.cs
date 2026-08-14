namespace PublishTool.Core.Models;

public sealed class IisSiteStatus
{
    public required string Name { get; set; }

    public required string Bindings { get; set; }

    /// <summary>"Started", "Stopped", or "Unknown", as reported by appcmd.</summary>
    public required string State { get; set; }

    /// <summary>The most recent deployment recorded for this site (see
    /// <see cref="Services.SiteDeploymentStore"/>), or null if PublishTool has never deployed to it
    /// -- the normal case for sites it didn't create, e.g. Default Web Site.</summary>
    public string? DeployedVersion { get; set; }

    public DateTimeOffset? DeployedAtUtc { get; set; }

    public string? DeployedBy { get; set; }

    public string? DeployedEnvironment { get; set; }
}
