using System.Text;
using System.Text.Json;

namespace PublishTool.Core.Services.BuildRunners;

/// <summary>
/// Shared "is this build release-shaped, and if so what signing args does it need" logic used by
/// both wrapper strategies. Signing is only ever applied to a release-shaped variant -- a debug
/// build always uses the native project's own debug keystore, untouched.
/// </summary>
internal static class AndroidSigning
{
    public static bool IsReleaseVariant(string buildVariant) =>
        buildVariant.Contains("release", StringComparison.OrdinalIgnoreCase);

    public static bool HasKeystore(AndroidBuildRequest request) =>
        !string.IsNullOrWhiteSpace(request.KeystorePath);

    /// <summary>Gradle/AGP's own external-signing override flags -- the same mechanism Android
    /// Studio's "Generate Signed Bundle/APK" wizard uses, recognized natively by AGP without
    /// touching the project's own build.gradle. Returns "" (nothing appended) unless this is a
    /// release-shaped build with a resolved keystore.</summary>
    public static string BuildGradleArgs(AndroidBuildRequest request)
    {
        if (!IsReleaseVariant(request.BuildVariant) || !HasKeystore(request))
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.Append($" -Pandroid.injected.signing.store.file=\"{request.KeystorePath}\"");
        sb.Append($" -Pandroid.injected.signing.store.password=\"{request.KeystorePassword}\"");
        sb.Append($" -Pandroid.injected.signing.key.alias=\"{request.KeyAlias}\"");
        sb.Append($" -Pandroid.injected.signing.key.password=\"{request.KeyPassword}\"");
        return sb.ToString();
    }

    /// <summary>Cordova has no Gradle-property-injection equivalent -- release signing there is
    /// driven by a JSON "build config" file (the same file/shape <c>cordova build --release
    /// --buildConfig=path</c> and Android Studio-generated Cordova projects already use), passed
    /// via <c>--buildConfig</c>. Writes one to a temp file and returns its path, or null if this
    /// build doesn't need one.</summary>
    public static string? WriteCordovaBuildConfig(AndroidBuildRequest request)
    {
        if (!IsReleaseVariant(request.BuildVariant) || !HasKeystore(request))
        {
            return null;
        }

        var buildConfig = new
        {
            android = new
            {
                release = new
                {
                    keystore = request.KeystorePath,
                    storePassword = request.KeystorePassword,
                    alias = request.KeyAlias,
                    password = request.KeyPassword,
                },
            },
        };

        var path = Path.Combine(Path.GetTempPath(), $"publishtool-cordova-build-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(buildConfig));
        return path;
    }
}
