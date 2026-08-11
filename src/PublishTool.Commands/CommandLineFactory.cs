using System.CommandLine;
using PublishTool.Core;
using PublishTool.Core.Models;
using PublishTool.Core.Services;

namespace PublishTool.Commands;

/// <summary>
/// Builds the command tree shared by the CLI and the GUI's embedded command panel,
/// so both surfaces run the exact same parsing and handler code.
/// </summary>
public static class CommandLineFactory
{
    public static RootCommand Create(IOutputSink output)
    {
        var root = new RootCommand("PublishTool - build, archive, and deploy publishes for .NET projects.");

        root.Add(BuildPublishCommand(output));
        root.Add(BuildAddProjectCommand(output));
        root.Add(BuildRemoveProjectCommand(output));
        root.Add(BuildListProjectsCommand(output));
        root.Add(BuildListBuildsCommand(output));
        root.Add(BuildSetBuildsRootCommand(output));
        root.Add(BuildSetMsBuildPathCommand(output));

        return root;
    }

    private static Command BuildPublishCommand(IOutputSink output)
    {
        var projectOption = new Option<string>("--project", "-p") { Description = "Registered project name.", Required = true };
        var versionOption = new Option<string>("--version", "-v") { Description = "Build version, e.g. 1.0.0.R0001B.", Required = true };

        var command = new Command("publish", "Publish a registered project: build, archive, and deploy to IIS.");
        command.Add(projectOption);
        command.Add(versionOption);

        command.SetAction(async (parseResult, ct) =>
        {
            var registry = new ProjectRegistry(ProjectRegistry.DefaultPath);
            var settings = AppSettings.Load(AppSettings.DefaultPath);

            var publisher = new Publisher(registry, output);
            var options = new PublishOptions
            {
                ProjectName = parseResult.GetValue(projectOption)!,
                Version = parseResult.GetValue(versionOption)!,
                BuildsRoot = settings.BuildsRoot,
                MsBuildPath = settings.MsBuildPath,
            };

            try
            {
                await publisher.PublishAsync(options, ct);
                return 0;
            }
            catch (Exception ex)
            {
                output.Error(ex.Message);
                return 1;
            }
        });

        return command;
    }

    private static Command BuildAddProjectCommand(IOutputSink output)
    {
        var nameOption = new Option<string>("--name", "-n") { Description = "Project name.", Required = true };
        var csprojOption = new Option<string>("--csproj") { Description = "Path to the .csproj file.", Required = true };
        var pubxmlOption = new Option<string>("--pubxml") { Description = "Publish profile name (e.g. FolderProfile).", Required = true };
        var assemblyInfoOption = new Option<string?>("--assembly-info") { Description = "Path to AssemblyInfo.cs, for version stamping (optional)." };
        var iisHostOption = new Option<string>("--iis-host") { Description = "Directory the latest build is mirrored to for IIS hosting.", Required = true };
        var extraTargetsOption = new Option<string?>("--extra-publish-targets")
        {
            Description = "Semicolon-separated MSBuild targets to force during publish, for packages whose " +
                           "own .targets don't hook into this toolset (e.g. CollectSQLiteInteropFiles). Optional.",
        };

        var command = new Command("add-project", "Register a project (or update an existing registration).");
        command.Add(nameOption);
        command.Add(csprojOption);
        command.Add(pubxmlOption);
        command.Add(assemblyInfoOption);
        command.Add(iisHostOption);
        command.Add(extraTargetsOption);

        command.SetAction(parseResult =>
        {
            var registry = new ProjectRegistry(ProjectRegistry.DefaultPath);
            registry.AddOrUpdate(new ProjectConfig
            {
                Name = parseResult.GetValue(nameOption)!,
                CsprojPath = parseResult.GetValue(csprojOption)!,
                PubxmlName = parseResult.GetValue(pubxmlOption)!,
                AssemblyInfoPath = parseResult.GetValue(assemblyInfoOption),
                IisHostPath = parseResult.GetValue(iisHostOption)!,
                ExtraPublishTargets = parseResult.GetValue(extraTargetsOption),
            });

            output.Info($"Registered project '{parseResult.GetValue(nameOption)}'.");
            return 0;
        });

        return command;
    }

    private static Command BuildRemoveProjectCommand(IOutputSink output)
    {
        var nameOption = new Option<string>("--name", "-n") { Description = "Project name to remove.", Required = true };

        var command = new Command("remove-project", "Unregister a project. Does not touch any files it produced.");
        command.Add(nameOption);

        command.SetAction(parseResult =>
        {
            var name = parseResult.GetValue(nameOption)!;
            var registry = new ProjectRegistry(ProjectRegistry.DefaultPath);

            if (registry.Remove(name))
            {
                output.Info($"Removed project '{name}'.");
                return 0;
            }

            output.Error($"No registered project named '{name}'.");
            return 1;
        });

        return command;
    }

    private static Command BuildListProjectsCommand(IOutputSink output)
    {
        var command = new Command("list-projects", "List registered projects.");

        command.SetAction(_ =>
        {
            var registry = new ProjectRegistry(ProjectRegistry.DefaultPath);
            if (registry.Projects.Count == 0)
            {
                output.Info("No projects registered yet. Use 'add-project' to register one.");
                return 0;
            }

            foreach (var project in registry.Projects)
            {
                output.Info($"{project.Name}  ->  {project.CsprojPath}  [{project.PubxmlName}]  host: {project.IisHostPath}");
            }

            return 0;
        });

        return command;
    }

    private static Command BuildListBuildsCommand(IOutputSink output)
    {
        var projectOption = new Option<string?>("--project", "-p") { Description = "Filter by project name (optional)." };

        var command = new Command("list-builds", "List archived builds from the build repository.");
        command.Add(projectOption);

        command.SetAction(parseResult =>
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            var buildRepository = new BuildRepository();
            var builds = buildRepository.ListBuilds(settings.BuildsRoot, parseResult.GetValue(projectOption));

            if (builds.Count == 0)
            {
                output.Info("No builds found.");
                return 0;
            }

            foreach (var build in builds)
            {
                output.Info($"{build.ProjectName}  v{build.Version}  {build.PublishedAtUtc:u}  by {build.PublishedBy}  -> {build.ZipPath}");
            }

            return 0;
        });

        return command;
    }

    private static Command BuildSetBuildsRootCommand(IOutputSink output)
    {
        var pathOption = new Option<string>("--path") { Description = "Directory where the build repository is stored.", Required = true };

        var command = new Command("set-builds-root", "Set the directory where archived builds are stored.");
        command.Add(pathOption);

        command.SetAction(parseResult =>
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            settings.BuildsRoot = parseResult.GetValue(pathOption)!;
            settings.Save(AppSettings.DefaultPath);

            output.Info($"Builds root set to '{settings.BuildsRoot}'.");
            return 0;
        });

        return command;
    }

    private static Command BuildSetMsBuildPathCommand(IOutputSink output)
    {
        var pathOption = new Option<string>("--path") { Description = "Full path to MSBuild.exe.", Required = true };

        var command = new Command("set-msbuild-path", "Override the auto-detected MSBuild.exe path.");
        command.Add(pathOption);

        command.SetAction(parseResult =>
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            settings.MsBuildPath = parseResult.GetValue(pathOption)!;
            settings.Save(AppSettings.DefaultPath);

            output.Info($"MSBuild path set to '{settings.MsBuildPath}'.");
            return 0;
        });

        return command;
    }
}
