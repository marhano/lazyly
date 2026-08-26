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

        if (string.IsNullOrWhiteSpace(project.PubxmlName))
        {
            throw new InvalidOperationException(
                $"'{project.Name}' has no publish profile (.pubxml) name configured -- set one in the project's Edit dialog before publishing.");
        }

        var msBuildExePath = await MsBuildLocator.LocateAsync(context.Options.MsBuildPath, ct);
        context.Output.Info($"Using MSBuild at {msBuildExePath}");

        context.Output.Stage("Running MSBuild publish...");
        var msBuild = new MsBuildRunner(context.Output);
        await msBuild.PublishAsync(
            msBuildExePath, project.CsprojPath!, project.PubxmlName, context.StagingDir,
            project.SdkStyleProject, project.ExtraPublishTargets, ct);

        return new BuildResult(BuildArtifactKind.Directory, context.StagingDir);
    }
}
