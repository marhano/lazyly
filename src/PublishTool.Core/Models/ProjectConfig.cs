using System.Text.Json.Serialization;

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

    /// <summary>What kind of project this is, and therefore which
    /// <see cref="Services.BuildRunners.IBuildRunner"/> builds it. Defaults to <see cref="ProjectType.DotNet"/>
    /// so every project registered before this field existed keeps behaving identically.</summary>
    public ProjectType ProjectType { get; set; } = ProjectType.DotNet;

    /// <summary>
    /// Local to this machine, even for a shared project in remote mode -- each dev's checkout
    /// lives at their own path. Optional: a project registered purely to deploy/monitor/manage
    /// (redeploy an existing build, read its Event Log, manage its IIS site or firewall rules)
    /// doesn't need one, since <see cref="Services.Publisher"/> is the only thing that requires it.
    /// Only meaningful when <see cref="ProjectType"/> is <see cref="ProjectType.DotNet"/>.
    /// </summary>
    public string? CsprojPath { get; set; }

    /// <summary>Only required when <see cref="ProjectType"/> is <see cref="ProjectType.DotNet"/> --
    /// enforced by <see cref="Services.Publisher"/>/the CLI's add-project validation, not by this
    /// model, so Angular/Android projects don't need to fake an MSBuild publish profile name.</summary>
    public string? PubxmlName { get; set; }

    public string? AssemblyInfoPath { get; set; }

    /// <summary>Build settings for an Angular project. Only populated when <see cref="ProjectType"/>
    /// is <see cref="ProjectType.Angular"/>.</summary>
    public AngularProjectSettings? Angular { get; set; }

    /// <summary>Build settings for a hybrid Capacitor/Cordova Android project. Only populated when
    /// <see cref="ProjectType"/> is <see cref="ProjectType.Android"/>.</summary>
    public AndroidProjectSettings? Android { get; set; }

    /// <summary>The one path <see cref="Services.Publisher"/> and git-checkout treat generically as
    /// "where this project's source lives", regardless of <see cref="ProjectType"/>. Angular/Android
    /// point at their root folder's package.json by convention rather than storing a separate file
    /// path, which keeps <see cref="Services.GitService"/>'s directory-from-file resolution unchanged.</summary>
    public string? SourceRootPath => ProjectType switch
    {
        ProjectType.DotNet => CsprojPath,
        ProjectType.Angular => string.IsNullOrWhiteSpace(Angular?.ProjectRootPath) ? null : Path.Combine(Angular.ProjectRootPath, "package.json"),
        ProjectType.Android => string.IsNullOrWhiteSpace(Android?.ProjectRootPath) ? null : Path.Combine(Android.ProjectRootPath, "package.json"),
        _ => null,
    };

    /// <summary>
    /// Extra MSBuild targets (semicolon-separated) to force alongside the default build/publish
    /// target, for projects whose package .targets files don't hook into this MSBuild toolset's
    /// publish pipeline on their own (e.g. "CollectSQLiteInteropFiles" for older SQLite packages).
    /// </summary>
    public string? ExtraPublishTargets { get; set; }

    /// <summary>Whether this dev has local IIS deployment turned on for this project at all.
    /// Local/per-user -- one dev may enable it while a teammate on the same project never deploys
    /// locally. Purely gates visibility/availability: when false, <see cref="LocalEnvironments"/>
    /// never appears as a deploy option on the Publish tab or Projects tab, regardless of what's
    /// configured in it.</summary>
    public bool LocalIisEnabled { get; set; }

    /// <summary>This dev's own deploy targets (e.g. a personal Staging site) for quick local
    /// testing -- named entries matching <see cref="EnvironmentSettings"/>. Local/per-user: one dev
    /// may configure a "Staging" environment here while a teammate on the same project never
    /// deploys locally at all. Only offered as a deploy option while <see cref="LocalIisEnabled"/>
    /// is true.</summary>
    public List<DeploymentEnvironment> LocalEnvironments { get; set; } = new();

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

    /// <summary>Whether the Event Logs tab is enabled for this project.</summary>
    public bool UseEventLog { get; set; }

    /// <summary>Windows Event Log name to read, e.g. "Application". Only meaningful when
    /// <see cref="UseEventLog"/> is true.</summary>
    public string? EventLogName { get; set; } = "Application";

    /// <summary>How to pick this project's entries out of the log -- "Source" filters natively by
    /// the log entry's Source/Provider name (clean, requires the app to log under its own distinct
    /// source); "MessageContains" matches a substring against the entry's message body instead
    /// (for apps that share a generic log, e.g. via NLog writing to "Application").</summary>
    public string? EventLogFilterType { get; set; } = "Source";

    /// <summary>The Source name(s) or message substring(s) to filter by, depending on
    /// <see cref="EventLogFilterType"/> -- an entry matches if it matches ANY value in this list.
    /// Current code always writes here (even for a single value); see
    /// <see cref="EventLogFilterValue"/> for the older field this supersedes.</summary>
    public List<string> EventLogFilterValues { get; set; } = new();

    /// <summary>Superseded by <see cref="EventLogFilterValues"/> -- kept only so a project saved
    /// before multi-value support existed still deserializes and filters correctly. Never written
    /// by current code; read only as a fallback, see <see cref="EffectiveEventLogFilterValues"/>.</summary>
    public string? EventLogFilterValue { get; set; }

    /// <summary><see cref="EventLogFilterValues"/>, falling back to the legacy single
    /// <see cref="EventLogFilterValue"/> for a project saved before multi-value support existed.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> EffectiveEventLogFilterValues =>
        EventLogFilterValues.Count > 0
            ? EventLogFilterValues
            : string.IsNullOrWhiteSpace(EventLogFilterValue) ? Array.Empty<string>() : new[] { EventLogFilterValue };

    /// <summary>Machine to read the event log from. Null/empty means the local machine.</summary>
    public string? EventLogMachineName { get; set; }

    /// <summary>Username for connecting to <see cref="EventLogMachineName"/>, if it requires
    /// different credentials than the current Windows identity. Null uses the current identity
    /// (the only option that works for a local read).</summary>
    public string? EventLogUsername { get; set; }

    /// <summary>DPAPI-protected (current-user-scoped) password for <see cref="EventLogUsername"/>,
    /// only present if the user opted to save it. Never stored in plain text. If this is null but
    /// a username is set, the GUI prompts for the password each time instead.</summary>
    public string? EventLogProtectedPassword { get; set; }

    /// <summary>Whether this dev has dev-server IIS deployment turned on for this project.
    /// Local/per-user, same reasoning as <see cref="LocalIisEnabled"/> -- each dev decides
    /// independently whether they want the dev-server deploy option available to them, even though
    /// <see cref="RemoteEnvironments"/> itself is shared team-wide. Purely gates visibility: when
    /// false, remote deploy never appears as an option regardless of what's configured.</summary>
    public bool RemoteIisEnabled { get; set; }

    /// <summary>The dev server's deploy targets for this project -- named entries matching
    /// <see cref="EnvironmentSettings"/>, e.g. "Staging" and "Production" each with their own root
    /// path and bindings. Shared team-wide (see <see cref="Services.RemoteProjectRegistry"/>) --
    /// the same targets apply no matter which teammate deploys. Only offered as a deploy option
    /// while <see cref="RemoteIisEnabled"/> is true (this dev's own choice) and remote mode is on.</summary>
    public List<DeploymentEnvironment> RemoteEnvironments { get; set; } = new();
}
