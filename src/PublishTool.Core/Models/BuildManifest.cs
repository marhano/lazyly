namespace PublishTool.Core.Models;

public sealed class BuildManifest
{
    public required string ProjectName { get; set; }

    public required string Version { get; set; }

    public required DateTimeOffset PublishedAtUtc { get; set; }

    public required string PublishedBy { get; set; }

    public required string ZipPath { get; set; }
}
