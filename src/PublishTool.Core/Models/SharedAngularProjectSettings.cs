namespace PublishTool.Core.Models;

/// <summary>
/// The team-wide subset of <see cref="AngularProjectSettings"/> -- everything except
/// <see cref="AngularProjectSettings.ProjectRootPath"/>, which is local to each dev's own checkout
/// (see <see cref="LocalProjectOverrides.AngularProjectRootPath"/>), same split as
/// <see cref="ProjectConfig.CsprojPath"/> vs <see cref="SharedProjectConfig.PubxmlName"/>.
/// </summary>
public sealed class SharedAngularProjectSettings
{
    public string? BuildConfiguration { get; set; } = "production";

    public string? WorkspaceProjectName { get; set; }
}
