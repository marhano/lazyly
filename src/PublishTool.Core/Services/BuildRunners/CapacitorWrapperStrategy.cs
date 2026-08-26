using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PublishTool.Core.Models;

namespace PublishTool.Core.Services.BuildRunners;

/// <summary>
/// Capacitor-wrapped Android app: build the web assets, sync them into the native `android/`
/// project Capacitor generated, then build that with the project's own Gradle wrapper. Signing
/// uses whatever the native project's own signingConfig already provides, unless the caller
/// resolved keystore details (project settings, or a one-off prompt) for a release-shaped build --
/// see <see cref="BuildSigningArgs"/>.
/// </summary>
public sealed partial class CapacitorWrapperStrategy : IAndroidWrapperStrategy
{
    public string TypeName => "Capacitor";

    public string DisplayName => "Capacitor";

    public bool Detect(string projectRoot) =>
        File.Exists(Path.Combine(projectRoot, "capacitor.config.json")) ||
        File.Exists(Path.Combine(projectRoot, "capacitor.config.ts"));

    public async Task<BuildResult> BuildAsync(AndroidBuildRequest request, string stagingDir, IOutputSink output, CancellationToken ct)
    {
        var projectRoot = request.ProjectRootPath;
        var androidDir = Path.Combine(projectRoot, "android");
        if (!Directory.Exists(androidDir))
        {
            throw new InvalidOperationException(
                $"Capacitor project detected at '{projectRoot}' but its native 'android' folder is missing -- " +
                "run 'npx cap add android' in the project first.");
        }

        var buildArgs = "run build --";
        if (!string.IsNullOrWhiteSpace(request.BuildConfiguration))
        {
            buildArgs += $" --configuration={request.BuildConfiguration}";
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

        var gradleTask = request.ArtifactType == AndroidArtifactType.Aab
            ? $"bundle{Capitalize(request.BuildVariant)}"
            : $"assemble{Capitalize(request.BuildVariant)}";
        var signingArgs = AndroidSigning.BuildGradleArgs(request);

        output.Stage($"Running gradlew {gradleTask}...");
        var gradleExit = await ShellCommandRunner.RunAsync($"gradlew.bat {gradleTask}{signingArgs}", androidDir, output, ct);
        if (gradleExit != 0)
        {
            throw new InvalidOperationException($"gradlew {gradleTask} exited with code {gradleExit}. See log output above for details.");
        }

        var artifactPath = AndroidArtifactLocator.Find(androidDir, request.BuildVariant, request.ArtifactType);
        return new BuildResult(BuildArtifactKind.SingleFile, artifactPath);
    }

    public AndroidAppMetadata ReadAppMetadata(string projectRoot)
    {
        var (bundleId, displayName) = ReadIdentity(projectRoot);
        var (versionNumber, buildNumber) = ReadGradleVersion(Path.Combine(projectRoot, "android"));
        return new AndroidAppMetadata { BundleId = bundleId, DisplayName = displayName, VersionNumber = versionNumber, BuildNumber = buildNumber };
    }

    public void WriteAppMetadata(string projectRoot, AndroidAppMetadata metadata)
    {
        WriteIdentity(projectRoot, metadata.BundleId, metadata.DisplayName);
        WriteGradleVersion(Path.Combine(projectRoot, "android"), metadata.VersionNumber, metadata.BuildNumber);
    }

    // capacitor.config.json's appId/appName are the single source of truth PublishTool edits --
    // "npx cap sync android" (already part of BuildAsync above) propagates them into the native
    // project's own files (AndroidManifest.xml, strings.xml, build.gradle's applicationId) on the
    // very next build, so nothing native needs touching directly for these two fields.
    private static (string? BundleId, string? DisplayName) ReadIdentity(string projectRoot)
    {
        var jsonPath = Path.Combine(projectRoot, "capacitor.config.json");
        if (File.Exists(jsonPath))
        {
            var root = JsonNode.Parse(File.ReadAllText(jsonPath)) as JsonObject;
            return (GetJsonString(root, "appId"), GetJsonString(root, "appName"));
        }

        var tsPath = Path.Combine(projectRoot, "capacitor.config.ts");
        if (File.Exists(tsPath))
        {
            var properties = TsObjectLiteral.Read(tsPath, "config");
            return (properties.GetValueOrDefault("appId"), properties.GetValueOrDefault("appName"));
        }

        return (null, null);
    }

    private static void WriteIdentity(string projectRoot, string? bundleId, string? displayName)
    {
        if (bundleId is null && displayName is null)
        {
            return;
        }

        var jsonPath = Path.Combine(projectRoot, "capacitor.config.json");
        if (File.Exists(jsonPath))
        {
            var root = JsonNode.Parse(File.ReadAllText(jsonPath)) as JsonObject ?? new JsonObject();
            if (bundleId is not null)
            {
                root["appId"] = bundleId;
            }

            if (displayName is not null)
            {
                root["appName"] = displayName;
            }

            File.WriteAllText(jsonPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        var tsPath = Path.Combine(projectRoot, "capacitor.config.ts");
        if (!File.Exists(tsPath))
        {
            return;
        }

        var updates = new Dictionary<string, string>();
        if (bundleId is not null)
        {
            updates["appId"] = bundleId;
        }

        if (displayName is not null)
        {
            updates["appName"] = displayName;
        }

        TsObjectLiteral.Write(tsPath, "config", updates);
    }

    private static string? GetJsonString(JsonObject? root, string key) =>
        root?[key] is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    // Capacitor doesn't sync versionName/versionCode from capacitor.config into the native
    // project the way it does appId/appName -- those live directly in the native module's own
    // build.gradle (Groovy) or build.gradle.kts (Kotlin DSL), so they're read/written there.
    private static (string? VersionName, string? VersionCode) ReadGradleVersion(string androidDir)
    {
        var gradlePath = FindAppBuildGradle(androidDir);
        if (gradlePath is null)
        {
            return (null, null);
        }

        var text = File.ReadAllText(gradlePath);
        var versionName = VersionNameRegex().Match(text) is { Success: true } nameMatch ? nameMatch.Groups["value"].Value : null;
        var versionCode = VersionCodeRegex().Match(text) is { Success: true } codeMatch ? codeMatch.Groups["value"].Value : null;
        return (versionName, versionCode);
    }

    private static void WriteGradleVersion(string androidDir, string? versionName, string? versionCode)
    {
        if (versionName is null && versionCode is null)
        {
            return;
        }

        var gradlePath = FindAppBuildGradle(androidDir);
        if (gradlePath is null)
        {
            return;
        }

        var lines = File.ReadAllLines(gradlePath);
        var changed = false;

        if (versionName is not null)
        {
            changed |= TryReplaceFirstMatch(lines, VersionNameRegex(), versionName);
        }

        if (versionCode is not null)
        {
            changed |= TryReplaceFirstMatch(lines, VersionCodeRegex(), versionCode);
        }

        if (changed)
        {
            File.WriteAllLines(gradlePath, lines);
        }
    }

    private static bool TryReplaceFirstMatch(string[] lines, Regex regex, string newValue)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var match = regex.Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            var group = match.Groups["value"];
            lines[i] = lines[i][..group.Index] + newValue + lines[i][(group.Index + group.Length)..];
            return true;
        }

        return false;
    }

    private static string? FindAppBuildGradle(string androidDir)
    {
        var candidates = new[]
        {
            Path.Combine(androidDir, "app", "build.gradle"),
            Path.Combine(androidDir, "app", "build.gradle.kts"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string Capitalize(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];

    [GeneratedRegex(@"versionName\s*=?\s*""(?<value>[^""]*)""")]
    private static partial Regex VersionNameRegex();

    [GeneratedRegex(@"versionCode\s*=?\s*(?<value>\d+)")]
    private static partial Regex VersionCodeRegex();
}
