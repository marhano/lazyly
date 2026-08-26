using System.Xml.Linq;
using PublishTool.Core.Models;

namespace PublishTool.Core.Services.BuildRunners;

/// <summary>
/// Cordova-wrapped Android app, optionally fronted by the Ionic CLI. Unlike Capacitor, one command
/// does the web build and the native build together -- Ionic's own build config (`--configuration`)
/// only applies when Ionic is present, since plain Cordova has no such concept.
/// </summary>
public sealed class CordovaWrapperStrategy : IAndroidWrapperStrategy
{
    private static readonly XNamespace AndroidNs = "http://schemas.android.com/apk/res/android";

    public string TypeName => "Cordova";

    public string DisplayName => "Cordova";

    public bool Detect(string projectRoot) => File.Exists(Path.Combine(projectRoot, "config.xml"));

    public async Task<BuildResult> BuildAsync(AndroidBuildRequest request, string stagingDir, IOutputSink output, CancellationToken ct)
    {
        var projectRoot = request.ProjectRootPath;
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

        var buildConfigPath = AndroidSigning.WriteCordovaBuildConfig(request);
        try
        {
            var platformArgs = new List<string>();
            if (usesIonic && !string.IsNullOrWhiteSpace(request.BuildConfiguration))
            {
                platformArgs.Add($"--configuration={request.BuildConfiguration}");
            }

            if (request.ArtifactType == AndroidArtifactType.Aab)
            {
                platformArgs.Add("--packageType=bundle");
            }

            if (buildConfigPath is not null)
            {
                platformArgs.Add($"--buildConfig=\"{buildConfigPath}\"");
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
        }
        finally
        {
            if (buildConfigPath is not null)
            {
                File.Delete(buildConfigPath);
            }
        }

        var platformDir = Path.Combine(projectRoot, "platforms", "android");
        var artifactPath = AndroidArtifactLocator.Find(platformDir, request.BuildVariant, request.ArtifactType);
        return new BuildResult(BuildArtifactKind.SingleFile, artifactPath);
    }

    public AndroidAppMetadata ReadAppMetadata(string projectRoot)
    {
        var configPath = Path.Combine(projectRoot, "config.xml");
        if (!File.Exists(configPath))
        {
            return new AndroidAppMetadata();
        }

        var doc = XDocument.Load(configPath);
        var widget = doc.Root;
        return new AndroidAppMetadata
        {
            BundleId = (string?)widget?.Attribute("id"),
            DisplayName = (string?)widget?.Elements().FirstOrDefault(e => e.Name.LocalName == "name"),
            VersionNumber = (string?)widget?.Attribute("version"),
            BuildNumber = (string?)widget?.Attribute(AndroidNs + "versionCode") ?? (string?)widget?.Attribute("android-versionCode"),
        };
    }

    public void WriteAppMetadata(string projectRoot, AndroidAppMetadata metadata)
    {
        var configPath = Path.Combine(projectRoot, "config.xml");
        if (!File.Exists(configPath))
        {
            return;
        }

        var doc = XDocument.Load(configPath);
        var widget = doc.Root;
        if (widget is null)
        {
            return;
        }

        if (metadata.BundleId is not null)
        {
            widget.SetAttributeValue("id", metadata.BundleId);
        }

        if (metadata.VersionNumber is not null)
        {
            widget.SetAttributeValue("version", metadata.VersionNumber);
        }

        if (metadata.BuildNumber is not null)
        {
            // Cordova has historically shipped both a plain "android-versionCode" attribute and the
            // namespaced "android:versionCode" form across template versions -- update whichever the
            // project actually has, defaulting to the plain form for a project with neither.
            if (widget.Attribute(AndroidNs + "versionCode") is not null)
            {
                widget.SetAttributeValue(AndroidNs + "versionCode", metadata.BuildNumber);
            }
            else
            {
                widget.SetAttributeValue("android-versionCode", metadata.BuildNumber);
            }
        }

        if (metadata.DisplayName is not null)
        {
            var nameElement = widget.Elements().FirstOrDefault(e => e.Name.LocalName == "name");
            if (nameElement is not null)
            {
                nameElement.Value = metadata.DisplayName;
            }
            else
            {
                widget.AddFirst(new XElement(widget.Name.Namespace + "name", metadata.DisplayName));
            }
        }

        doc.Save(configPath);
    }
}
