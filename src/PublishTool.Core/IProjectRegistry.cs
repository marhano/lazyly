using PublishTool.Core.Models;

namespace PublishTool.Core;

/// <summary>
/// Where a project's config comes from -- either the local <see cref="ProjectRegistry"/> file, or
/// <see cref="Services.RemoteProjectRegistry"/> merging shared team config from the Remote Build
/// Hosting API with this dev's own local overrides. See <see cref="Services.ProjectRegistryFactory"/>.
///
/// Fully async, even though the local implementation is just file I/O: WPF has a synchronization
/// context, and blocking on the remote implementation's HTTP calls from the UI thread would risk
/// deadlock. Every caller awaits regardless of which implementation is actually in play.
/// </summary>
public interface IProjectRegistry
{
    Task<IReadOnlyList<ProjectConfig>> GetProjectsAsync(CancellationToken ct = default);

    Task<ProjectConfig?> GetAsync(string name, CancellationToken ct = default);

    Task AddOrUpdateAsync(ProjectConfig config, CancellationToken ct = default);

    Task<bool> RemoveAsync(string name, CancellationToken ct = default);

    /// <summary>Atomically reserves and returns the next release-notes sequence number for a
    /// project, persisting the increment. The only supported way to advance
    /// <see cref="ProjectConfig.LastReleaseNotesSequence"/> -- doing it as a separate read + local
    /// mutation + <see cref="AddOrUpdateAsync"/> (the old pattern) is racy once the registry might
    /// be shared across a team.</summary>
    Task<int> ReserveNextReleaseSequenceAsync(string projectName, CancellationToken ct = default);
}
