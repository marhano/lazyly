namespace PublishTool.Core.Models;

public sealed class IisSiteStatus
{
    public required string Name { get; set; }

    public required string Bindings { get; set; }

    /// <summary>"Started", "Stopped", or "Unknown", as reported by appcmd.</summary>
    public required string State { get; set; }
}
