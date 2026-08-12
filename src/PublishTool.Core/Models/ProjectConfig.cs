namespace PublishTool.Core.Models;

public sealed class ProjectConfig
{
    public required string Name { get; set; }

    /// <summary>
    /// Short project code used as the prefix of the release notes reference number, e.g. "BPS"
    /// produces references like "BPS-2026-0007". Release notes are only generated at publish
    /// time when this is set.
    /// </summary>
    public string? ProjectId { get; set; }

    /// <summary>
    /// The most recently issued release notes sequence number for this project, tracked so each
    /// publish gets the next number in order (e.g. ...-0007, then ...-0008). Managed by the
    /// publisher -- never set this directly when registering or editing a project.
    /// </summary>
    public int LastReleaseNotesSequence { get; set; }

    public required string CsprojPath { get; set; }

    public required string PubxmlName { get; set; }

    public string? AssemblyInfoPath { get; set; }

    public required string IisHostPath { get; set; }

    /// <summary>
    /// Extra MSBuild targets (semicolon-separated) to force alongside the default build/publish
    /// target, for projects whose package .targets files don't hook into this MSBuild toolset's
    /// publish pipeline on their own (e.g. "CollectSQLiteInteropFiles" for older SQLite packages).
    /// </summary>
    public string? ExtraPublishTargets { get; set; }

    /// <summary>
    /// When true, publish ensures an IIS site named after this project exists before mirroring
    /// files into <see cref="IisHostPath"/> -- creating one with <see cref="IisBindings"/> if
    /// it's not already there. Never modifies an existing site.
    /// </summary>
    public bool AutoCreateIisSite { get; set; }

    public List<IisBinding> IisBindings { get; set; } = new();

    /// <summary>
    /// True for modern SDK-style projects (e.g. ASP.NET Core), which publish via an explicit
    /// Publish target and PublishDir. False (default) for classic .NET Framework Web Deploy
    /// projects, which publish via DeployOnBuild and PublishUrl -- mixing the two conventions
    /// causes an MSBuild circular-dependency error, so this must be set correctly per project.
    /// </summary>
    public bool SdkStyleProject { get; set; }

    /// <summary>
    /// Whether this project's builds should appear in the build-hosting site's listing. Builds
    /// are always archived to the build repository either way; this only controls visibility
    /// there. Stamped onto each build's manifest at publish time, so the hosting site (which may
    /// run on a different machine) only needs to read manifests, not this registry.
    /// </summary>
    public bool ListInHosting { get; set; } = true;

    /// <summary>
    /// Whether this project has a config file holding user-visible settings (e.g. a version
    /// number shown in the app's UI) that PublishTool can edit directly at publish time --
    /// distinct from <see cref="AssemblyInfoPath"/>, which stamps the .NET assembly version, not
    /// anything end users see.
    /// </summary>
    public bool UseAppConfig { get; set; }

    /// <summary>Identifies which <see cref="Services.AppConfig.IAppConfigProvider"/> to use, e.g.
    /// "WebConfigAppSettings". Only meaningful when <see cref="UseAppConfig"/> is true.</summary>
    public string? AppConfigType { get; set; }

    /// <summary>Path to the config file itself, e.g. the project's Web.config.</summary>
    public string? AppConfigPath { get; set; }
}
