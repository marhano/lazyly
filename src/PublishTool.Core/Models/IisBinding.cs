namespace PublishTool.Core.Models;

public sealed class IisBinding
{
    /// <summary>"http" or "https".</summary>
    public required string Protocol { get; set; }

    /// <summary>"*" for All Unassigned.</summary>
    public required string IpAddress { get; set; }

    public required int Port { get; set; }

    public string? HostName { get; set; }
}
