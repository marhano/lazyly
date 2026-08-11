namespace PublishTool.Core.Models;

public sealed class PublishOptions
{
    public required string ProjectName { get; set; }

    public required string Version { get; set; }

    public required string BuildsRoot { get; set; }

    public string? MsBuildPath { get; set; }
}
