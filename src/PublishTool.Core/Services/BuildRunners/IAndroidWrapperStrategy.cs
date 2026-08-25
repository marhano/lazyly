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

    Task<BuildResult> BuildAsync(AndroidProjectSettings settings, string stagingDir, IOutputSink output, CancellationToken ct);
}
