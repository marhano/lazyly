namespace PublishTool.Core.Models;

/// <summary>
/// Build settings for a <see cref="ProjectType.Angular"/> project. Only populated when
/// <see cref="ProjectConfig.ProjectType"/> is <see cref="ProjectType.Angular"/>. Deliberately has no
/// build-configuration field -- that's decided per publish instead (see
/// <see cref="PublishOptions.BuildConfiguration"/>), normally derived from whichever
/// environment.*.ts file gets picked for app config (see
/// <see cref="Services.AppConfig.EnvironmentTsProvider.InferBuildConfiguration"/>).
/// </summary>
public sealed class AngularProjectSettings
{
    /// <summary>The project's root folder (where package.json/angular.json live) -- not a specific
    /// file. Local to this machine, same reasoning as <see cref="ProjectConfig.CsprojPath"/>.
    /// Optional for the same reason <c>CsprojPath</c> is: a teammate's shared project record may not
    /// have this dev's local checkout path configured yet, and a project registered purely to
    /// monitor/manage an existing build doesn't need one at all.</summary>
    public string? ProjectRootPath { get; set; }

    /// <summary>For Angular workspace/monorepo setups with more than one buildable project --
    /// passed as an extra argument to the build script. Null for a single-app repo.</summary>
    public string? WorkspaceProjectName { get; set; }
}
