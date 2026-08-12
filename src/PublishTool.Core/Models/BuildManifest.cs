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

    /// <summary>
    /// Absolute path to this build's release notes .txt, or null if none was generated (the
    /// project had no Project ID set at publish time, or this build predates the feature).
    /// Deliberately kept out of the zip -- it's meant to be browsable/downloadable on its own
    /// from the build-hosting site, not bundled inside the deployed package.
    /// </summary>
    public string? ReleaseNotesPath { get; set; }

    /// <summary>
    /// The app-config key/value settings (e.g. Web.config appSettings) this build was published
    /// with, if the project uses app config -- so re-selecting this version in the Publish tab
    /// shows exactly what was published, instead of whatever the config file currently has on
    /// disk (which may have since moved on to a newer version).
    /// </summary>
    public Dictionary<string, string>? AppConfigSettings { get; set; }
}
