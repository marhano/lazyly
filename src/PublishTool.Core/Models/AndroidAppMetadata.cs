namespace PublishTool.Core.Models;

/// <summary>
/// The handful of app-identity fields Android Studio itself exposes prominently (bundle/package
/// id, display name, version name, version code) -- found and edited via
/// <see cref="Services.BuildRunners.IAndroidWrapperStrategy.ReadAppMetadata"/>/<c>WriteAppMetadata</c>
/// rather than through the generic <see cref="Services.AppConfig.IAppConfigProvider"/> mechanism,
/// since these live in wrapper-specific native project files (Capacitor: capacitor.config +
/// android/app/build.gradle; Cordova: config.xml), not a single arbitrary config file the user
/// points at. Null on any field means "leave it alone" on write, or "couldn't find it" on read.
/// </summary>
public sealed class AndroidAppMetadata
{
    /// <summary>The app's package name / application id, e.g. "com.example.myapp".</summary>
    public string? BundleId { get; set; }

    /// <summary>The user-visible app name shown under its icon.</summary>
    public string? DisplayName { get; set; }

    /// <summary>The user-visible version string (Android's <c>versionName</c>), e.g. "1.0.1".</summary>
    public string? VersionNumber { get; set; }

    /// <summary>The internal, monotonically-increasing build number (Android's <c>versionCode</c>)
    /// Play Store upload requires to strictly increase release over release.</summary>
    public string? BuildNumber { get; set; }
}
