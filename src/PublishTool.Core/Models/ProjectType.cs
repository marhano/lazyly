namespace PublishTool.Core.Models;

/// <summary>
/// What kind of project this is, and therefore which <see cref="Services.BuildRunners.IBuildRunner"/>
/// builds it. <see cref="DotNet"/> is the default (0) so every project registered before this field
/// existed -- which never set it -- keeps behaving exactly as before with no migration needed.
/// </summary>
public enum ProjectType
{
    DotNet = 0,
    Angular = 1,

    /// <summary>
    /// A hybrid web project wrapped by Capacitor or Cordova (optionally via the Ionic CLI) that
    /// builds to an installable Android APK/AAB. Not "native Android" -- see
    /// <see cref="Services.BuildRunners.IAndroidWrapperStrategy"/> for the actual build mechanics.
    /// </summary>
    Android = 2,
}
