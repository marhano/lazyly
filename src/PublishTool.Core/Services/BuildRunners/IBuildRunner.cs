using PublishTool.Core.Models;

namespace PublishTool.Core.Services.BuildRunners;

/// <summary>
/// Builds a project's source into an artifact ready to archive/deploy, regardless of what kind of
/// project it is. One implementation per <see cref="ProjectType"/> -- <see cref="BuildRunnerRegistry"/>
/// is the extension point for adding more later, mirroring
/// <see cref="AppConfig.IAppConfigProvider"/>/<see cref="AppConfig.AppConfigProviderRegistry"/>.
/// </summary>
public interface IBuildRunner
{
    ProjectType ProjectType { get; }

    /// <summary>Shown in build-progress messages, e.g. "MSBuild", "Angular".</summary>
    string DisplayName { get; }

    Task<BuildResult> BuildAsync(BuildContext context, CancellationToken ct);
}

/// <summary>Whether a build produced a directory of files (deployable to IIS, archived as a zip)
/// or a single file (e.g. an APK/AAB, archived as-is).</summary>
public enum BuildArtifactKind
{
    Directory,
    SingleFile,
}

/// <summary>Everything an <see cref="IBuildRunner"/> needs to build one project. <paramref name="StagingDir"/>
/// is a fresh, empty temp directory <see cref="Publisher"/> owns the lifetime of -- a
/// <see cref="BuildArtifactKind.Directory"/> result is expected to have written its output there
/// (or somewhere the runner reports back via <see cref="BuildResult.Path"/>); a
/// <see cref="BuildArtifactKind.SingleFile"/> result just needs to point at wherever the build tool
/// actually put the file.</summary>
public sealed record BuildContext(ProjectConfig Project, PublishOptions Options, string StagingDir, IOutputSink Output);

public sealed record BuildResult(BuildArtifactKind ArtifactKind, string Path);
