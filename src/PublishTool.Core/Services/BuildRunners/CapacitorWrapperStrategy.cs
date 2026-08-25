using PublishTool.Core.Models;

namespace PublishTool.Core.Services.BuildRunners;

/// <summary>
/// Capacitor-wrapped Android app: build the web assets, sync them into the native `android/`
/// project Capacitor generated, then build that with the project's own Gradle wrapper (which uses
/// whatever signing config already exists there -- this tool never touches keystores/passwords).
/// </summary>
public sealed class CapacitorWrapperStrategy : IAndroidWrapperStrategy
{
    public string TypeName => "Capacitor";

    public string DisplayName => "Capacitor";

    public bool Detect(string projectRoot) =>
        File.Exists(Path.Combine(projectRoot, "capacitor.config.json")) ||
        File.Exists(Path.Combine(projectRoot, "capacitor.config.ts"));

    public async Task<BuildResult> BuildAsync(AndroidProjectSettings settings, string stagingDir, IOutputSink output, CancellationToken ct)
    {
        var projectRoot = settings.ProjectRootPath!;
        var androidDir = Path.Combine(projectRoot, "android");
        if (!Directory.Exists(androidDir))
        {
            throw new InvalidOperationException(
                $"Capacitor project detected at '{projectRoot}' but its native 'android' folder is missing -- " +
                "run 'npx cap add android' in the project first.");
        }

        var buildArgs = "run build --";
        if (!string.IsNullOrWhiteSpace(settings.BuildConfiguration))
        {
            buildArgs += $" --configuration={settings.BuildConfiguration}";
        }

        output.Stage("Running npm run build...");
        var buildExit = await ShellCommandRunner.RunAsync($"npm {buildArgs}", projectRoot, output, ct);
        if (buildExit != 0)
        {
            throw new InvalidOperationException($"npm run build exited with code {buildExit}. See log output above for details.");
        }

        output.Stage("Syncing web build into the Android project (npx cap sync android)...");
        var syncExit = await ShellCommandRunner.RunAsync("npx cap sync android", projectRoot, output, ct);
        if (syncExit != 0)
        {
            throw new InvalidOperationException($"npx cap sync android exited with code {syncExit}. See log output above for details.");
        }

        var gradleTask = settings.ArtifactType == AndroidArtifactType.Aab
            ? $"bundle{Capitalize(settings.BuildVariant)}"
            : $"assemble{Capitalize(settings.BuildVariant)}";

        output.Stage($"Running gradlew {gradleTask}...");
        var gradleExit = await ShellCommandRunner.RunAsync($"gradlew.bat {gradleTask}", androidDir, output, ct);
        if (gradleExit != 0)
        {
            throw new InvalidOperationException($"gradlew {gradleTask} exited with code {gradleExit}. See log output above for details.");
        }

        var artifactPath = AndroidArtifactLocator.Find(androidDir, settings.BuildVariant, settings.ArtifactType);
        return new BuildResult(BuildArtifactKind.SingleFile, artifactPath);
    }

    private static string Capitalize(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
