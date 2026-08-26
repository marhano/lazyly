using PublishTool.Core.Models;

namespace PublishTool.Core.Services.BuildRunners;

/// <summary>
/// Builds an Android APK/AAB out of a hybrid web project wrapped by a specific native tool
/// (Capacitor, Cordova). This is the actual extensibility seam for Android support -- not the web
/// frontend framework, which is already invisible at this layer (every strategy just runs whatever
/// the project's own "build" script/CLI does; Angular today, Vue/React/anything else tomorrow needs
/// no changes here at all). Add a new wrapper by implementing this and listing it in
/// <see cref="AndroidWrapperStrategyRegistry"/>.
/// </summary>
public interface IAndroidWrapperStrategy
{
    string TypeName { get; }

    string DisplayName { get; }

    /// <summary>Looks for this wrapper's marker file(s) directly under <paramref name="projectRoot"/>.
    /// Re-run at every build (and by the GUI for its informational "Detected: ..." label) rather than
    /// cached/persisted, so it can never go stale if a project's wrapper changes.</summary>
    bool Detect(string projectRoot);

    Task<BuildResult> BuildAsync(AndroidBuildRequest request, string stagingDir, IOutputSink output, CancellationToken ct);

    /// <summary>Reads the app-identity fields Android Studio's own UI shows prominently (bundle
    /// id, display name, version name/code) from wherever this wrapper actually keeps them.
    /// Best-effort -- any field it can't find comes back null, not an exception.</summary>
    AndroidAppMetadata ReadAppMetadata(string projectRoot);

    /// <summary>Writes back only the non-null fields of <paramref name="metadata"/>, same
    /// "only touch what's given" contract as <see cref="AppConfig.IAppConfigProvider.WriteSettings"/>.</summary>
    void WriteAppMetadata(string projectRoot, AndroidAppMetadata metadata);
}

/// <summary>Everything an <see cref="IAndroidWrapperStrategy"/> needs to build one Android publish.
/// <see cref="BuildConfiguration"/>/<see cref="BuildVariant"/>/<see cref="ArtifactType"/> are
/// per-publish choices (see <see cref="PublishOptions.BuildConfiguration"/> etc.), not project
/// settings -- <see cref="ProjectRootPath"/> is the only thing that's actually fixed per project.
/// The signing fields are resolved by the caller (from <see cref="Models.AndroidProjectSettings"/>,
/// or a one-off prompt if unset) and only actually used for a "release"-shaped variant -- see each
/// wrapper's own <c>BuildAsync</c> for exactly how they're passed to Gradle.</summary>
public sealed record AndroidBuildRequest(
    string ProjectRootPath,
    string? BuildConfiguration,
    string BuildVariant,
    AndroidArtifactType ArtifactType,
    string? KeystorePath = null,
    string? KeystorePassword = null,
    string? KeyAlias = null,
    string? KeyPassword = null);
