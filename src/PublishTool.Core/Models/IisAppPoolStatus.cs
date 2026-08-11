namespace PublishTool.Core.Models;

public sealed class IisAppPoolStatus
{
    public required string Name { get; set; }

    public required string ManagedRuntimeVersion { get; set; }

    public required string PipelineMode { get; set; }

    /// <summary>"Started", "Stopped", or "Unknown", as reported by appcmd.</summary>
    public required string State { get; set; }
}
