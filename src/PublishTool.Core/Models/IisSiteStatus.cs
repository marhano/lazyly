using System.Text.Json.Serialization;

namespace PublishTool.Core.Models;

public sealed class IisSiteStatus
{
    public required string Name { get; set; }

    /// <summary>"[PT] {Name}" if PublishTool has ever deployed to this site (see
    /// <see cref="DeployedVersion"/>), otherwise just <see cref="Name"/> -- purely a display hint
    /// for the IIS tab's grid so a PublishTool-managed site is visually distinguishable from one it
    /// never touched. Computed, not persisted -- <see cref="Name"/> (the real appcmd site identity,
    /// used everywhere deploys/history/actions key off it) is never altered.</summary>
    [JsonIgnore]
    public string DisplayName => DeployedVersion is not null ? $"[PT] {Name}" : Name;

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
