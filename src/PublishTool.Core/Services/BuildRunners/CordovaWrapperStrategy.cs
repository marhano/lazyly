using PublishTool.Core.Models;

namespace PublishTool.Core.Services.BuildRunners;

/// <summary>
/// Cordova-wrapped Android app, optionally fronted by the Ionic CLI. Unlike Capacitor, one command
/// does the web build and the native build together -- Ionic's own build config (`--configuration`)
/// only applies when Ionic is present, since plain Cordova has no such concept.
/// </summary>
public sealed class CordovaWrapperStrategy : IAndroidWrapperStrategy
{
    public string TypeName => "Cordova";

    public string DisplayName => "Cordova";

    public bool Detect(string projectRoot) => File.Exists(Path.Combine(projectRoot, "config.xml"));

    public async Task<BuildResult> BuildAsync(AndroidProjectSettings settings, string stagingDir, IOutputSink output, CancellationToken ct)
    {
        var projectRoot = settings.ProjectRootPath!;
        var usesIonic = File.Exists(Path.Combine(projectRoot, "ionic.config.json"));

        var args = new List<string>();
        if (usesIonic)
        {
            args.AddRange(["ionic", "cordova", "build", "android", "--prod"]);
        }
        else
        {
            args.AddRange(["cordova", "build", "android", "--release"]);
        }

        var platformArgs = new List<string>();
        if (usesIonic && !string.IsNullOrWhiteSpace(settings.BuildConfiguration))
        {
            platformArgs.Add($"--configuration={settings.BuildConfiguration}");
        }

        if (settings.ArtifactType == AndroidArtifactType.Aab)
        {
            platformArgs.Add("--packageType=bundle");
        }

        if (platformArgs.Count > 0)
        {
            args.Add("--");
            args.AddRange(platformArgs);
        }

        var commandLabel = usesIonic ? "ionic cordova build android" : "cordova build android";
        output.Stage($"Running {commandLabel}...");
        var exitCode = await ShellCommandRunner.RunAsync(string.Join(' ', args), projectRoot, output, ct);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"{commandLabel} exited with code {exitCode}. See log output above for details.");
        }

        var platformDir = Path.Combine(projectRoot, "platforms", "android");
        var artifactPath = AndroidArtifactLocator.Find(platformDir, settings.BuildVariant, settings.ArtifactType);
        return new BuildResult(BuildArtifactKind.SingleFile, artifactPath);
    }
}
