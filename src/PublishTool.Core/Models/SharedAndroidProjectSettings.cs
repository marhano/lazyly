namespace PublishTool.Core.Models;

/// <summary>
/// The team-wide subset of <see cref="AndroidProjectSettings"/> -- everything except
/// <see cref="AndroidProjectSettings.ProjectRootPath"/>, which is local to each dev's own checkout
/// (see <see cref="LocalProjectOverrides.AndroidProjectRootPath"/>).
/// </summary>
public sealed class SharedAndroidProjectSettings
{
    public string? BuildConfiguration { get; set; } = "production";

    public string BuildVariant { get; set; } = "release";

    public AndroidArtifactType ArtifactType { get; set; } = AndroidArtifactType.Apk;
}
