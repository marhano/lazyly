namespace PublishTool.Core.Models;

/// <summary>
/// Build settings for a <see cref="ProjectType.Android"/> project. Only populated when
/// <see cref="ProjectConfig.ProjectType"/> is <see cref="ProjectType.Android"/>. Deliberately has no
/// wrapper-type field (Capacitor vs Cordova) -- that's auto-detected from
/// <see cref="ProjectRootPath"/> at build time by <see cref="Services.BuildRunners.IAndroidWrapperStrategy"/>,
/// so it can never go stale. Signing/keystore stays the native project's own responsibility -- this
/// tool never stores signing secrets.
/// </summary>
public sealed class AndroidProjectSettings
{
    /// <summary>The hybrid app's root folder (where package.json and the Capacitor/Cordova config
    /// file live) -- not a specific file. Local to this machine. Optional for the same reason
    /// <see cref="AngularProjectSettings.ProjectRootPath"/> is.</summary>
    public string? ProjectRootPath { get; set; }

    /// <summary>Passed through to the web build step. Defaults to "production".</summary>
    public string? BuildConfiguration { get; set; } = "production";

    /// <summary>Gradle build variant, e.g. "release". v1 supports exactly one variant per
    /// registered project -- multiple flavors need separate project registrations.</summary>
    public string BuildVariant { get; set; } = "release";

    public AndroidArtifactType ArtifactType { get; set; } = AndroidArtifactType.Apk;
}
