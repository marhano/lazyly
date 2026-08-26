namespace PublishTool.Core.Models;

/// <summary>
/// Build settings for a <see cref="ProjectType.Android"/> project. Only populated when
/// <see cref="ProjectConfig.ProjectType"/> is <see cref="ProjectType.Android"/>. Deliberately has no
/// wrapper-type field (Capacitor vs Cordova) -- that's auto-detected from
/// <see cref="ProjectRootPath"/> at build time by <see cref="Services.BuildRunners.IAndroidWrapperStrategy"/>,
/// so it can never go stale. Also deliberately has no build-configuration/build-variant/
/// artifact-type fields -- those are all decided per publish instead (see
/// <see cref="PublishOptions.BuildConfiguration"/>/<see cref="PublishOptions.AndroidBuildVariant"/>/
/// <see cref="PublishOptions.AndroidArtifactType"/>).
///
/// Signing is the one exception to "nothing but where the app lives" -- optional keystore details
/// for release builds, entered via the GUI's signing dialog (mirroring Android Studio's own
/// "Generate Signed Bundle/APK" fields) so a release build can actually be signed without every
/// dev needing the project's own build.gradle to already have a signingConfig wired up. Everything
/// here is local to this machine, never shared -- same reasoning as <see cref="ProjectRootPath"/>,
/// only more so since two of these are secrets. Passwords are DPAPI-protected
/// (see <see cref="Services.SecretProtector.AndroidSigningPurpose"/>), never stored in plain text.
/// </summary>
public sealed class AndroidProjectSettings
{
    /// <summary>The hybrid app's root folder (where package.json and the Capacitor/Cordova config
    /// file live) -- not a specific file. Local to this machine. Optional for the same reason
    /// <see cref="AngularProjectSettings.ProjectRootPath"/> is.</summary>
    public string? ProjectRootPath { get; set; }

    /// <summary>Path to the .jks/.keystore file used to sign release builds. Optional -- if unset,
    /// a release build just uses the native project's own signingConfig (if any), same as before
    /// this existed.</summary>
    public string? KeystorePath { get; set; }

    /// <summary>The key alias within <see cref="KeystorePath"/> to sign with.</summary>
    public string? KeyAlias { get; set; }

    /// <summary>DPAPI-protected keystore password.</summary>
    public string? ProtectedKeystorePassword { get; set; }

    /// <summary>DPAPI-protected password for <see cref="KeyAlias"/> (often the same as the
    /// keystore password, but not always).</summary>
    public string? ProtectedKeyPassword { get; set; }
}
