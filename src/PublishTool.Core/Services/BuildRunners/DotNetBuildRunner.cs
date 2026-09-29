using PublishTool.Core.Models;

namespace PublishTool.Core.Services.BuildRunners;

/// <summary>
/// A pure extraction of what <see cref="Publisher"/> did inline before <see cref="IBuildRunner"/>
/// existed -- locate MSBuild, run the publish profile, hand back the staging directory. Zero
/// behavior change from before this abstraction was introduced.
/// </summary>
public sealed class DotNetBuildRunner : IBuildRunner
{
    public ProjectType ProjectType => ProjectType.DotNet;

    public string DisplayName => "MSBuild";

    public async Task<BuildResult> BuildAsync(BuildContext context, CancellationToken ct)
    {
        var project = context.Project;

        // The Publish tab's profile select (context.Options.PubxmlNameOverride) is the normal
        // source now -- project.PubxmlName only remains as a fallback for CLI callers that set it
        // via add-project instead of passing --pubxml-name per publish.
        var pubxmlName = string.IsNullOrWhiteSpace(context.Options.PubxmlNameOverride)
            ? project.PubxmlName
            : context.Options.PubxmlNameOverride;

        if (string.IsNullOrWhiteSpace(pubxmlName))
        {
            throw new InvalidOperationException(
                $"'{project.Name}' has no publish profile (.pubxml) selected -- pick one on the Publish tab before publishing.");
        }

        var msBuildExePath = await MsBuildLocator.LocateAsync(context.Options.MsBuildPath, ct);
        context.Output.Info($"Using MSBuild at {msBuildExePath}");

        // The profile's own <LastUsedBuildConfiguration> (e.g. "Release-AsensoPay" for a
        // multi-brand project with per-brand DefineConstants/config transforms) -- MSBuild's web
        // publish pipeline doesn't infer this from /p:PublishProfile alone, so without reading and
        // passing it explicitly every profile silently built as plain "Release" regardless of which
        // one was picked (the actual bug behind "picking a different profile doesn't seem to apply").
        var configuration = PublishProfileDiscovery.ReadBuildConfiguration(project.CsprojPath, pubxmlName) ?? "Release";
        context.Output.Info($"Using publish profile '{pubxmlName}' (configuration '{configuration}')");

        context.Output.Stage("Running MSBuild publish...");
        var msBuild = new MsBuildRunner(context.Output);
        await msBuild.PublishAsync(
            msBuildExePath, project.CsprojPath!, pubxmlName, configuration, context.StagingDir,
            project.SdkStyleProject, project.ExtraPublishTargets, ct);

        return new BuildResult(BuildArtifactKind.Directory, context.StagingDir);
    }
}
