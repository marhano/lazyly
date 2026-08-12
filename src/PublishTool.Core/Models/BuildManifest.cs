namespace PublishTool.Core.Models;

public sealed class BuildManifest
{
    public required string ProjectName { get; set; }

    public required string Version { get; set; }

    public required DateTimeOffset PublishedAtUtc { get; set; }

    public required string PublishedBy { get; set; }

    public required string ZipPath { get; set; }

    /// <summary>
    /// Copied from the project's ListInHosting setting at publish time, so the hosting site can
    /// filter by reading manifests alone -- not required, so older manifests without this field
    /// still deserialize fine and default to listed.
    /// </summary>
    public bool ListInHosting { get; set; } = true;
}
