namespace PublishTool.Core.Models;

/// <summary>
/// One entry in the Projects tab's audit trail (see <see cref="Services.ProjectAuditStore"/>) --
/// a single global log covering every kind of project-related action (add/remove a project, change
/// its settings, publish, deploy an existing build, delete a build, mark one as latest), same
/// "one flat log, not one file per project" reasoning as <see cref="FirewallAuditEntry"/>: a removed
/// project still needs to stay visible in its own history.
/// </summary>
public sealed class ProjectAuditEntry
{
    /// <summary>"Added", "Removed", "Settings Updated", "Published", "Deployed", "Build Deleted",
    /// or "Marked Latest".</summary>
    public required string Action { get; set; }

    public required string ProjectName { get; set; }

    /// <summary>Free-form context for the action, e.g. a version number, a deploy target/environment
    /// name, or a version+environment pair. Null when the action itself is self-explanatory.</summary>
    public string? Details { get; set; }

    public required DateTimeOffset PerformedAtUtc { get; set; }

    public required string PerformedBy { get; set; }
}
