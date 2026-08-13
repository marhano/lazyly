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

    public required string PubxmlName { get; set; }

    public string? ExtraPublishTargets { get; set; }

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

    /// <summary>IIS host path on the dev server itself -- see <see cref="ProjectConfig.RemoteIisHostPath"/>.</summary>
    public string? RemoteIisHostPath { get; set; }

    public List<IisBinding> RemoteIisBindings { get; set; } = new();

    public bool RemoteAutoCreateIisSite { get; set; }

    /// <summary>Extracts the shared half of a full <see cref="ProjectConfig"/> -- the only place
    /// that mapping should happen, since it needs to stay in sync from two call sites:
    /// <see cref="Services.RemoteProjectRegistry.AddOrUpdateAsync"/> (editing a project while in
    /// remote registry mode) and <see cref="Services.Publisher.PublishAsync"/> (which must push
    /// this project's shared config -- specifically its dev-server deploy target -- to the Hosting
    /// server before asking it to deploy, even when the publishing dev is in local registry mode;
    /// otherwise the server has never heard of a project it's being asked to deploy for).</summary>
    public static SharedProjectConfig FromProjectConfig(ProjectConfig config) => new()
    {
        Name = config.Name,
        ProjectId = config.ProjectId,
        LastReleaseNotesSequence = config.LastReleaseNotesSequence,
        PubxmlName = config.PubxmlName,
        ExtraPublishTargets = config.ExtraPublishTargets,
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
        RemoteIisHostPath = config.RemoteIisHostPath,
        RemoteIisBindings = config.RemoteIisBindings,
        RemoteAutoCreateIisSite = config.RemoteAutoCreateIisSite,
    };
}
