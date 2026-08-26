using PublishTool.Core.Models;

namespace PublishTool.Core.Services.BuildRunners;

/// <summary>
/// Dispatches to whichever native wrapper (Capacitor, Cordova) is auto-detected at
/// <see cref="AndroidProjectSettings.ProjectRootPath"/> -- see <see cref="IAndroidWrapperStrategy"/>
/// for why there's no stored wrapper-type field to keep in sync.
/// </summary>
public sealed class AndroidBuildRunner : IBuildRunner
{
    public ProjectType ProjectType => ProjectType.Android;

    public string DisplayName => "Android";

    public Task<BuildResult> BuildAsync(BuildContext context, CancellationToken ct)
    {
        var project = context.Project;
        var settings = project.Android
            ?? throw new InvalidOperationException(
                $"'{project.Name}' is registered as an Android project but has no Android settings configured.");

        if (string.IsNullOrWhiteSpace(settings.ProjectRootPath))
        {
            throw new InvalidOperationException(
                $"'{project.Name}' has no project root folder configured -- set one in the project's Edit dialog before publishing.");
        }

        var wrapper = AndroidWrapperStrategyRegistry.Detect(settings.ProjectRootPath)
            ?? throw new InvalidOperationException(
                $"'{project.Name}': couldn't detect a Capacitor or Cordova project at '{settings.ProjectRootPath}' -- " +
                "expected a capacitor.config.json/.ts or config.xml file there.");

        context.Output.Info($"Detected {wrapper.DisplayName} project.");

        // Decrypted here (not earlier) so it only ever exists in memory for the duration of this
        // one build call -- the build always runs on the same machine that registered the
        // project, even in remote mode (remote mode only changes where the finished artifact is
        // archived/uploaded to, never where the build itself runs), so DPAPI's user+machine
        // scoping is always satisfied.
        var keystorePassword = settings.ProtectedKeystorePassword is { } protectedKeystorePassword
            ? SecretProtector.TryUnprotect(protectedKeystorePassword, SecretProtector.AndroidSigningPurpose)
            : null;
        var keyPassword = settings.ProtectedKeyPassword is { } protectedKeyPassword
            ? SecretProtector.TryUnprotect(protectedKeyPassword, SecretProtector.AndroidSigningPurpose)
            : null;

        var request = new AndroidBuildRequest(
            settings.ProjectRootPath, context.Options.BuildConfiguration, context.Options.AndroidBuildVariant, context.Options.AndroidArtifactType,
            settings.KeystorePath, keystorePassword, settings.KeyAlias, keyPassword);
        return wrapper.BuildAsync(request, context.StagingDir, context.Output, ct);
    }
}
