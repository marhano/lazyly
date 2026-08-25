using PublishTool.Core.Models;

namespace PublishTool.Core.Services.BuildRunners;

/// <summary>
/// Builds an Angular project via its own <c>npm run build</c> script -- deliberately not <c>ng
/// build</c> directly, so this works for any project whose "build" script wraps Angular (or, later,
/// something else entirely) however it wants. Passes an explicit <c>--output-path</c> so the build
/// lands directly in the staging directory, instead of parsing angular.json to find its configured
/// output path.
/// </summary>
public sealed class AngularBuildRunner : IBuildRunner
{
    public ProjectType ProjectType => ProjectType.Angular;

    public string DisplayName => "Angular";

    public async Task<BuildResult> BuildAsync(BuildContext context, CancellationToken ct)
    {
        var project = context.Project;
        var settings = project.Angular
            ?? throw new InvalidOperationException(
                $"'{project.Name}' is registered as an Angular project but has no Angular settings configured.");

        Directory.CreateDirectory(context.StagingDir);

        var args = new List<string> { "run", "build", "--" };
        if (!string.IsNullOrWhiteSpace(settings.WorkspaceProjectName))
        {
            args.Add(settings.WorkspaceProjectName);
        }

        if (!string.IsNullOrWhiteSpace(settings.BuildConfiguration))
        {
            args.Add($"--configuration={settings.BuildConfiguration}");
        }

        args.Add($"--output-path=\"{context.StagingDir}\"");

        context.Output.Stage("Running npm run build...");
        var exitCode = await ShellCommandRunner.RunAsync(
            "npm " + string.Join(' ', args), settings.ProjectRootPath!, context.Output, ct);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"npm run build exited with code {exitCode}. See log output above for details.");
        }

        return new BuildResult(BuildArtifactKind.Directory, context.StagingDir);
    }
}
