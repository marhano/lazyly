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
        root.Add(BuildSetThemeCommand(output));
        root.Add(BuildSetAccentColorCommand(output));
        root.Add(BuildIisListCommand(output));
        root.Add(BuildIisSiteActionCommand(output, "iis-start-site", "Start an IIS site.", start: true));
        root.Add(BuildIisSiteActionCommand(output, "iis-stop-site", "Stop an IIS site.", start: false));
        root.Add(BuildIisAppPoolActionCommand(output, "iis-start-apppool", "Start an IIS application pool.", AppPoolAction.Start));
        root.Add(BuildIisAppPoolActionCommand(output, "iis-stop-apppool", "Stop an IIS application pool.", AppPoolAction.Stop));
        root.Add(BuildIisAppPoolActionCommand(output, "iis-recycle-apppool", "Recycle an IIS application pool.", AppPoolAction.Recycle));

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
        var autoCreateIisSiteOption = new Option<bool>("--auto-create-iis-site")
        {
            Description = "Create an IIS site named after this project on publish, if one doesn't already exist " +
                           "(requires at least one --iis-binding). Never modifies an existing site.",
        };
        var iisBindingOption = new Option<string[]>("--iis-binding")
        {
            Description = "A site binding as protocol:ip:port:hostname, e.g. http:*:80: or https:*:443:example.com " +
                           "(hostname may be empty). Repeatable.",
        };
        var sdkStyleOption = new Option<bool>("--sdk-style-project")
        {
            Description = "Set for modern SDK-style projects (e.g. ASP.NET Core), which publish differently than " +
                           "classic .NET Framework Web Deploy projects. Leave unset for classic projects.",
        };
        var listInHostingOption = new Option<bool>("--list-in-hosting")
        {
            Description = "Whether this project's builds appear in the build-hosting site's listing. " +
                           "Builds are always archived either way; this only controls visibility there.",
            DefaultValueFactory = _ => true,
        };

        var command = new Command("add-project", "Register a project (or update an existing registration).");
        command.Add(nameOption);
        command.Add(csprojOption);
        command.Add(pubxmlOption);
        command.Add(assemblyInfoOption);
        command.Add(iisHostOption);
        command.Add(extraTargetsOption);
        command.Add(autoCreateIisSiteOption);
        command.Add(iisBindingOption);
        command.Add(sdkStyleOption);
        command.Add(listInHostingOption);

        command.SetAction(parseResult =>
        {
            try
            {
                var bindings = (parseResult.GetValue(iisBindingOption) ?? Array.Empty<string>())
                    .Select(ParseIisBinding)
                    .ToList();

                var registry = new ProjectRegistry(ProjectRegistry.DefaultPath);
                registry.AddOrUpdate(new ProjectConfig
                {
                    Name = parseResult.GetValue(nameOption)!,
                    CsprojPath = parseResult.GetValue(csprojOption)!,
                    PubxmlName = parseResult.GetValue(pubxmlOption)!,
                    AssemblyInfoPath = parseResult.GetValue(assemblyInfoOption),
                    IisHostPath = parseResult.GetValue(iisHostOption)!,
                    ExtraPublishTargets = parseResult.GetValue(extraTargetsOption),
                    AutoCreateIisSite = parseResult.GetValue(autoCreateIisSiteOption),
                    IisBindings = bindings,
                    SdkStyleProject = parseResult.GetValue(sdkStyleOption),
                    ListInHosting = parseResult.GetValue(listInHostingOption),
                });

                output.Info($"Registered project '{parseResult.GetValue(nameOption)}'.");
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

    private static IisBinding ParseIisBinding(string raw)
    {
        var parts = raw.Split(':', 4);
        if (parts.Length < 3 || string.IsNullOrWhiteSpace(parts[0]) || !int.TryParse(parts[2], out var port))
        {
            throw new ArgumentException(
                $"Invalid --iis-binding value '{raw}'. Expected protocol:ip:port:hostname, " +
                "e.g. http:*:80: or https:*:443:example.com.");
        }

        return new IisBinding
        {
            Protocol = parts[0],
            IpAddress = string.IsNullOrWhiteSpace(parts[1]) ? "*" : parts[1],
            Port = port,
            HostName = parts.Length > 3 && !string.IsNullOrWhiteSpace(parts[3]) ? parts[3] : null,
        };
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

    private static Command BuildSetThemeCommand(IOutputSink output)
    {
        var valueOption = new Option<string>("--value") { Description = "Light, Dark, or System.", Required = true };

        var command = new Command("set-theme", "Set the app's color theme (persisted; GUI applies it live).");
        command.Add(valueOption);

        command.SetAction(parseResult =>
        {
            var value = parseResult.GetValue(valueOption)!;
            if (!string.Equals(value, "Light", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(value, "Dark", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(value, "System", StringComparison.OrdinalIgnoreCase))
            {
                output.Error("Theme must be Light, Dark, or System.");
                return 1;
            }

            var settings = AppSettings.Load(AppSettings.DefaultPath);
            settings.Theme = string.Equals(value, "System", StringComparison.OrdinalIgnoreCase) ? null : value;
            settings.Save(AppSettings.DefaultPath);

            output.Info($"Theme set to '{value}'.");
            return 0;
        });

        return command;
    }

    private static Command BuildSetAccentColorCommand(IOutputSink output)
    {
        var presetNames = string.Join(", ", AccentPresets.All.Select(p => p.Name));
        var valueOption = new Option<string>("--value") { Description = $"One of: {presetNames}.", Required = true };

        var command = new Command("set-accent-color", "Set the app's accent color (persisted; GUI applies it live).");
        command.Add(valueOption);

        command.SetAction(parseResult =>
        {
            var value = parseResult.GetValue(valueOption)!;
            var preset = AccentPresets.All.FirstOrDefault(p => string.Equals(p.Name, value, StringComparison.OrdinalIgnoreCase));
            if (preset.Hex is null)
            {
                output.Error($"Accent color must be one of: {presetNames}.");
                return 1;
            }

            var settings = AppSettings.Load(AppSettings.DefaultPath);
            settings.AccentColor = preset.Hex;
            settings.Save(AppSettings.DefaultPath);

            output.Info($"Accent color set to '{preset.Name}'.");
            return 0;
        });

        return command;
    }

    private static Command BuildIisListCommand(IOutputSink output)
    {
        var command = new Command("iis-list", "List IIS sites and application pools with their current state.");

        command.SetAction(async (_, ct) =>
        {
            try
            {
                var manager = new IisSiteManager(output);

                var sites = await manager.ListSitesAsync(ct);
                output.Info(sites.Count == 0 ? "No sites." : "Sites:");
                foreach (var site in sites)
                {
                    output.Info($"  {site.Name}  [{site.State}]  bindings: {site.Bindings}");
                }

                var pools = await manager.ListAppPoolsAsync(ct);
                output.Info(pools.Count == 0 ? "No application pools." : "Application pools:");
                foreach (var pool in pools)
                {
                    output.Info($"  {pool.Name}  [{pool.State}]  {pool.ManagedRuntimeVersion} / {pool.PipelineMode}");
                }

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

    private static Command BuildIisSiteActionCommand(IOutputSink output, string commandName, string description, bool start)
    {
        var nameOption = new Option<string>("--name", "-n") { Description = "Site name.", Required = true };
        var command = new Command(commandName, description);
        command.Add(nameOption);

        command.SetAction(async (parseResult, ct) =>
        {
            try
            {
                var manager = new IisSiteManager(output);
                var name = parseResult.GetValue(nameOption)!;

                if (start)
                {
                    await manager.StartSiteAsync(name, ct);
                }
                else
                {
                    await manager.StopSiteAsync(name, ct);
                }

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

    private static Command BuildIisAppPoolActionCommand(IOutputSink output, string commandName, string description, AppPoolAction action)
    {
        var nameOption = new Option<string>("--name", "-n") { Description = "Application pool name.", Required = true };
        var command = new Command(commandName, description);
        command.Add(nameOption);

        command.SetAction(async (parseResult, ct) =>
        {
            try
            {
                var manager = new IisSiteManager(output);
                var name = parseResult.GetValue(nameOption)!;

                switch (action)
                {
                    case AppPoolAction.Start:
                        await manager.StartAppPoolAsync(name, ct);
                        break;
                    case AppPoolAction.Stop:
                        await manager.StopAppPoolAsync(name, ct);
                        break;
                    case AppPoolAction.Recycle:
                        await manager.RecycleAppPoolAsync(name, ct);
                        break;
                }

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

    private enum AppPoolAction { Start, Stop, Recycle }
}
