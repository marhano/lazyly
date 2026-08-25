namespace PublishTool.Core.Models;

/// <summary>
/// The team-wide subset of <see cref="ProjectConfig"/> -- what the Remote Build Hosting API's
/// <c>/api/projects</c> surface stores/returns. Deliberately excludes anything that's a fact about
/// one dev's own machine (local paths, local IIS target, per-user automation toggles) -- see the
/// field split in <see cref="Services.RemoteProjectRegistry"/>.
/// </summary>
public sealed class SharedProjectConfig
{
    public required string Name { get; set; }

    public string? ProjectId { get; set; }

    public int LastReleaseNotesSequence { get; set; }

    /// <summary>See <see cref="ProjectConfig.ProjectType"/>.</summary>
    public ProjectType ProjectType { get; set; } = ProjectType.DotNet;

    /// <summary>Only required when <see cref="ProjectType"/> is <see cref="ProjectType.DotNet"/> --
    /// see <see cref="ProjectConfig.PubxmlName"/>.</summary>
    public string? PubxmlName { get; set; }

    public string? ExtraPublishTargets { get; set; }

    /// <summary>The team-wide subset of <see cref="ProjectConfig.Angular"/> -- excludes
    /// <see cref="AngularProjectSettings.ProjectRootPath"/>, which lives in
    /// <see cref="LocalProjectOverrides.AngularProjectRootPath"/>, same split as
    /// <see cref="ProjectConfig.CsprojPath"/> vs <see cref="PubxmlName"/>.</summary>
    public SharedAngularProjectSettings? Angular { get; set; }

    /// <summary>The team-wide subset of <see cref="ProjectConfig.Android"/> -- see
    /// <see cref="Angular"/> above for the same root-path split.</summary>
    public SharedAndroidProjectSettings? Android { get; set; }

    public bool SdkStyleProject { get; set; }

    public bool ListInHosting { get; set; } = true;

    public bool UseAppConfig { get; set; }

    public string? AppConfigType { get; set; }

    public bool UseEventLog { get; set; }

    public string? EventLogName { get; set; } = "Application";

    public string? EventLogFilterType { get; set; } = "Source";

    public string? EventLogFilterValue { get; set; }

    public string? EventLogMachineName { get; set; }

    public string? EventLogUsername { get; set; }

    /// <summary>The dev server's named deploy targets for this project -- see
    /// <see cref="ProjectConfig.RemoteEnvironments"/>.</summary>
    public List<DeploymentEnvironment> RemoteEnvironments { get; set; } = new();

    /// <summary>Extracts the shared half of a full <see cref="ProjectConfig"/> -- the only place
    /// that mapping should happen, since <see cref="Services.RemoteProjectRegistry.AddOrUpdateAsync"/>
    /// needs it kept in sync with every new shared field this model gains.</summary>
    public static SharedProjectConfig FromProjectConfig(ProjectConfig config) => new()
    {
        Name = config.Name,
        ProjectId = config.ProjectId,
        LastReleaseNotesSequence = config.LastReleaseNotesSequence,
        ProjectType = config.ProjectType,
        PubxmlName = config.PubxmlName,
        ExtraPublishTargets = config.ExtraPublishTargets,
        Angular = config.Angular is null ? null : new SharedAngularProjectSettings
        {
            BuildConfiguration = config.Angular.BuildConfiguration,
            WorkspaceProjectName = config.Angular.WorkspaceProjectName,
        },
        Android = config.Android is null ? null : new SharedAndroidProjectSettings
        {
            BuildConfiguration = config.Android.BuildConfiguration,
            BuildVariant = config.Android.BuildVariant,
            ArtifactType = config.Android.ArtifactType,
        },
        SdkStyleProject = config.SdkStyleProject,
        ListInHosting = config.ListInHosting,
        UseAppConfig = config.UseAppConfig,
        AppConfigType = config.AppConfigType,
        UseEventLog = config.UseEventLog,
        EventLogName = config.EventLogName,
        EventLogFilterType = config.EventLogFilterType,
        EventLogFilterValue = config.EventLogFilterValue,
        EventLogMachineName = config.EventLogMachineName,
        EventLogUsername = config.EventLogUsername,
        RemoteEnvironments = config.RemoteEnvironments,
    };
}
