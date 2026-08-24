using System.CommandLine;
using PublishTool.Core;
using PublishTool.Core.Models;
using PublishTool.Core.Services;
using PublishTool.Core.Services.AppConfig;

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
        root.Add(BuildGitCheckoutCommand(output));
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
        var gitBranchOption = new Option<string?>("--git-branch")
        {
            Description = "Git branch to check out before building (creates a local tracking branch " +
                           "from origin/<branch> if it's not local yet). Optional -- omit to build whatever's currently checked out.",
        };
        var featureOption = new Option<string[]>("--feature")
        {
            Description = "A \"Features and Enhancements\" release notes entry. Repeatable.",
        };
        var fixOption = new Option<string[]>("--fix")
        {
            Description = "A \"Fixes\" release notes entry. Repeatable.",
        };
        var otherUpdateOption = new Option<string[]>("--other-update")
        {
            Description = "An \"Other Updates\" release notes entry. Repeatable.",
        };
        var backlogItemOption = new Option<string[]>("--backlog-item")
        {
            Description = "A \"Backlog Items\" release notes entry. Repeatable.",
        };
        var appConfigSettingOption = new Option<string[]>("--app-config-setting")
        {
            Description = "An app config key=value pair to write before building, for projects with " +
                           "app config enabled (see add-project --app-config-type). Repeatable.",
        };
        var markLatestOption = new Option<bool>("--mark-latest")
        {
            Description = "Flag this build as the project's \"latest release\" on the hosting site, " +
                           "un-flagging whichever build previously held that. At most one build per project can be latest.",
        };
        var deployTargetOption = new Option<string?>("--deploy-target")
        {
            Description = "Which side to deploy this publish to: \"local\" (this machine's IIS) or \"remote\" " +
                           "(the dev server's IIS). Requires --environment. Omit either to archive/upload only.",
        };
        var environmentOption = new Option<string?>("--environment", "-e")
        {
            Description = "Named deploy target (e.g. Staging, Production) within whichever side --deploy-target " +
                           "selects, matching an entry in the project's local or dev-server environments.",
        };

        var command = new Command("publish", "Publish a registered project: build, archive, and deploy to IIS.");
        command.Add(projectOption);
        command.Add(versionOption);
        command.Add(gitBranchOption);
        command.Add(featureOption);
        command.Add(fixOption);
        command.Add(otherUpdateOption);
        command.Add(backlogItemOption);
        command.Add(appConfigSettingOption);
        command.Add(markLatestOption);
        command.Add(deployTargetOption);
        command.Add(environmentOption);

        command.SetAction(async (parseResult, ct) =>
        {
            var deployTargetRaw = parseResult.GetValue(deployTargetOption);
            var environmentName = parseResult.GetValue(environmentOption);

            DeployTarget deployTarget;
            if (string.IsNullOrWhiteSpace(deployTargetRaw))
            {
                deployTarget = DeployTarget.None;
            }
            else if (string.Equals(deployTargetRaw, "local", StringComparison.OrdinalIgnoreCase))
            {
                deployTarget = DeployTarget.Local;
            }
            else if (string.Equals(deployTargetRaw, "remote", StringComparison.OrdinalIgnoreCase))
            {
                deployTarget = DeployTarget.Remote;
            }
            else
            {
                output.Error($"Unknown --deploy-target '{deployTargetRaw}'. Valid values: local, remote.");
                return 1;
            }

            if (deployTarget != DeployTarget.None && string.IsNullOrWhiteSpace(environmentName))
            {
                output.Error("--environment is required when --deploy-target is set.");
                return 1;
            }

            if (deployTarget == DeployTarget.None && !string.IsNullOrWhiteSpace(environmentName))
            {
                output.Error("--deploy-target is required when --environment is set.");
                return 1;
            }

            var registry = ProjectRegistryFactory.Create();
            var settings = AppSettings.Load(AppSettings.DefaultPath);

            var publisher = new Publisher(registry, output);
            var options = new PublishOptions
            {
                ProjectName = parseResult.GetValue(projectOption)!,
                Version = parseResult.GetValue(versionOption)!,
                BuildsRoot = settings.BuildsRoot,
                MsBuildPath = settings.MsBuildPath,
                GitBranch = parseResult.GetValue(gitBranchOption),
                ReleaseNotesFeatures = (parseResult.GetValue(featureOption) ?? Array.Empty<string>()).ToList(),
                ReleaseNotesFixes = (parseResult.GetValue(fixOption) ?? Array.Empty<string>()).ToList(),
                ReleaseNotesOtherUpdates = (parseResult.GetValue(otherUpdateOption) ?? Array.Empty<string>()).ToList(),
                ReleaseNotesBacklogItems = (parseResult.GetValue(backlogItemOption) ?? Array.Empty<string>()).ToList(),
                MarkAsLatest = parseResult.GetValue(markLatestOption),
                DeployTarget = deployTarget,
                DeployEnvironmentName = environmentName,
                // Whether this uploads to the dev server instead of archiving locally is decided
                // purely by the global "Use dev server for projects" setting -- see
                // PublishOptions.UseRemoteMode and Publisher itself.
                UseRemoteMode = settings.UseRemoteMode,
                RemoteHostingUrl = settings.RemoteHostingUrl,
#pragma warning disable CA1416 // DPAPI (SecretProtector) is Windows-only; this whole tool (MSBuild, IIS, appcmd) only ever runs on Windows despite PublishTool.Commands' plain net8.0 TFM.
                RemoteHostingApiKey = settings.RemoteHostingProtectedApiKey is null
                    ? null
                    : SecretProtector.TryUnprotect(settings.RemoteHostingProtectedApiKey, SecretProtector.RemoteHostingPurpose),
#pragma warning restore CA1416
            };

            try
            {
                options.AppConfigSettings = ParseKeyValuePairs(parseResult.GetValue(appConfigSettingOption) ?? Array.Empty<string>());
            }
            catch (ArgumentException ex)
            {
                output.Error(ex.Message);
                return 1;
            }

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

    private static Command BuildGitCheckoutCommand(IOutputSink output)
    {
        var projectOption = new Option<string>("--project", "-p") { Description = "Registered project name.", Required = true };
        var branchOption = new Option<string>("--branch", "-b") { Description = "Branch to check out.", Required = true };

        var command = new Command("git-checkout", "Check out a git branch in a registered project's repo, " +
                                                    "without building or publishing anything.");
        command.Add(projectOption);
        command.Add(branchOption);

        command.SetAction(async (parseResult, ct) =>
        {
            var registry = ProjectRegistryFactory.Create();
            var projectName = parseResult.GetValue(projectOption)!;
            var project = await registry.GetAsync(projectName, ct);
            if (project is null)
            {
                output.Error($"Project '{projectName}' is not registered.");
                return 1;
            }

            if (string.IsNullOrWhiteSpace(project.CsprojPath))
            {
                output.Error($"'{project.Name}' has no .csproj path configured.");
                return 1;
            }

            try
            {
                await new GitService(output).CheckoutAsync(project.CsprojPath, parseResult.GetValue(branchOption)!, ct);
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

    private static Dictionary<string, string> ParseKeyValuePairs(IEnumerable<string> raw)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in raw)
        {
            var separatorIndex = entry.IndexOf('=');
            if (separatorIndex <= 0)
            {
                throw new ArgumentException($"Invalid --app-config-setting value '{entry}'. Expected key=value.");
            }

            result[entry[..separatorIndex]] = entry[(separatorIndex + 1)..];
        }

        return result;
    }

    private static Command BuildAddProjectCommand(IOutputSink output)
    {
        var nameOption = new Option<string>("--name", "-n") { Description = "Project name.", Required = true };
        var projectIdOption = new Option<string?>("--project-id")
        {
            Description = "Short project code used as the release notes reference prefix, e.g. \"BPS\" " +
                           "produces references like BPS-2026-0007. Optional -- release notes are only " +
                           "generated at publish time when this is set.",
        };
        var csprojOption = new Option<string?>("--csproj")
        {
            Description = "Path to the .csproj file. Optional -- omit for a project registered purely to " +
                           "deploy/monitor/manage (redeploy an existing build, Event Logs, IIS, firewall rules); " +
                           "publishing requires this to be set first.",
        };
        var pubxmlOption = new Option<string>("--pubxml") { Description = "Publish profile name (e.g. FolderProfile).", Required = true };
        var assemblyInfoOption = new Option<string?>("--assembly-info") { Description = "Path to AssemblyInfo.cs, for version stamping (optional)." };
        var extraTargetsOption = new Option<string?>("--extra-publish-targets")
        {
            Description = "Semicolon-separated MSBuild targets to force during publish, for packages whose " +
                           "own .targets don't hook into this toolset (e.g. CollectSQLiteInteropFiles). Optional.",
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
        var appConfigTypeNames = string.Join(", ", AppConfigProviderRegistry.All.Select(p => p.TypeName));
        var appConfigTypeOption = new Option<string?>("--app-config-type")
        {
            Description = $"Enables editing this project's user-visible config (e.g. a version shown in its UI) " +
                           $"from the Publish tab. One of: {appConfigTypeNames}. Requires --app-config-path.",
        };
        var appConfigPathOption = new Option<string?>("--app-config-path")
        {
            Description = "Path to the config file (e.g. Web.config) for --app-config-type. Required if that's set.",
        };
        var enableEventLogOption = new Option<bool>("--enable-event-log")
        {
            Description = "Enables the Event Logs tab for this project.",
        };
        var eventLogNameOption = new Option<string?>("--event-log-name")
        {
            Description = "Windows Event Log name to read, e.g. \"Application\" (the default).",
        };
        var eventLogFilterTypeOption = new Option<string?>("--event-log-filter-type")
        {
            Description = $"How to pick this project's entries out of the log: \"{EventLogFilterTypes.Source}\" " +
                           $"(native filter by Source/Provider name) or \"{EventLogFilterTypes.MessageContains}\" " +
                           "(substring match against the message body, for apps sharing a generic log via e.g. NLog).",
        };
        var eventLogFilterValueOption = new Option<string?>("--event-log-filter-value")
        {
            Description = "The Source name or message substring to filter by, per --event-log-filter-type.",
        };
        var eventLogMachineOption = new Option<string?>("--event-log-machine")
        {
            Description = "Machine to read the event log from. Omit for the local machine.",
        };
        var eventLogUsernameOption = new Option<string?>("--event-log-username")
        {
            Description = "Username for --event-log-machine, if it needs different credentials than the current " +
                           "Windows identity. The password itself is set (and optionally saved) from the GUI, not the CLI.",
        };

        var command = new Command("add-project", "Register a project (or update an existing registration).");
        command.Add(nameOption);
        command.Add(projectIdOption);
        command.Add(csprojOption);
        command.Add(pubxmlOption);
        command.Add(assemblyInfoOption);
        command.Add(extraTargetsOption);
        command.Add(sdkStyleOption);
        command.Add(listInHostingOption);
        command.Add(appConfigTypeOption);
        command.Add(appConfigPathOption);
        command.Add(enableEventLogOption);
        command.Add(eventLogNameOption);
        command.Add(eventLogFilterTypeOption);
        command.Add(eventLogFilterValueOption);
        command.Add(eventLogMachineOption);
        command.Add(eventLogUsernameOption);

        command.SetAction(async (parseResult, ct) =>
        {
            try
            {
                var appConfigType = parseResult.GetValue(appConfigTypeOption);
                var appConfigPath = parseResult.GetValue(appConfigPathOption);
                if (appConfigType is not null)
                {
                    if (AppConfigProviderRegistry.Get(appConfigType) is null)
                    {
                        output.Error($"Unknown --app-config-type '{appConfigType}'. Valid values: {appConfigTypeNames}.");
                        return 1;
                    }

                    if (string.IsNullOrWhiteSpace(appConfigPath))
                    {
                        output.Error("--app-config-path is required when --app-config-type is set.");
                        return 1;
                    }
                }

                var enableEventLog = parseResult.GetValue(enableEventLogOption);
                var eventLogFilterType = parseResult.GetValue(eventLogFilterTypeOption);
                if (enableEventLog && eventLogFilterType is not null &&
                    !string.Equals(eventLogFilterType, EventLogFilterTypes.Source, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(eventLogFilterType, EventLogFilterTypes.MessageContains, StringComparison.OrdinalIgnoreCase))
                {
                    output.Error($"Unknown --event-log-filter-type '{eventLogFilterType}'. " +
                                 $"Valid values: {EventLogFilterTypes.Source}, {EventLogFilterTypes.MessageContains}.");
                    return 1;
                }

                var registry = ProjectRegistryFactory.Create();
                var name = parseResult.GetValue(nameOption)!;
                // add-project doubles as "update" -- preserve the release notes sequence counter
                // and any saved event log password across edits instead of resetting them, since
                // there's no CLI option for either.
                var existing = await registry.GetAsync(name, ct);

                await registry.AddOrUpdateAsync(new ProjectConfig
                {
                    Name = name,
                    ProjectId = parseResult.GetValue(projectIdOption),
                    LastReleaseNotesSequence = existing?.LastReleaseNotesSequence ?? 0,
                    CsprojPath = parseResult.GetValue(csprojOption),
                    PubxmlName = parseResult.GetValue(pubxmlOption)!,
                    AssemblyInfoPath = parseResult.GetValue(assemblyInfoOption),
                    ExtraPublishTargets = parseResult.GetValue(extraTargetsOption),
                    // Environments (local and dev-server deploy targets) have no CLI surface -- an
                    // add-project update must preserve whatever's already configured via the GUI
                    // dialog instead of wiping it out just because this command can't express it.
                    LocalEnvironments = existing?.LocalEnvironments ?? new(),
                    RemoteEnvironments = existing?.RemoteEnvironments ?? new(),
                    SdkStyleProject = parseResult.GetValue(sdkStyleOption),
                    ListInHosting = parseResult.GetValue(listInHostingOption),
                    UseAppConfig = appConfigType is not null,
                    AppConfigType = appConfigType,
                    AppConfigPath = appConfigPath,
                    UseEventLog = enableEventLog,
                    EventLogName = parseResult.GetValue(eventLogNameOption) ?? "Application",
                    EventLogFilterType = eventLogFilterType ?? EventLogFilterTypes.Source,
                    EventLogFilterValue = parseResult.GetValue(eventLogFilterValueOption),
                    EventLogMachineName = parseResult.GetValue(eventLogMachineOption),
                    EventLogUsername = parseResult.GetValue(eventLogUsernameOption),
                    EventLogProtectedPassword = existing?.EventLogProtectedPassword,
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

    private static Command BuildRemoveProjectCommand(IOutputSink output)
    {
        var nameOption = new Option<string>("--name", "-n") { Description = "Project name to remove.", Required = true };

        var command = new Command("remove-project", "Unregister a project. Does not touch any files it produced.");
        command.Add(nameOption);

        command.SetAction(async (parseResult, ct) =>
        {
            var name = parseResult.GetValue(nameOption)!;
            var registry = ProjectRegistryFactory.Create();

            if (await registry.RemoveAsync(name, ct))
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

        command.SetAction(async (_, ct) =>
        {
            var registry = ProjectRegistryFactory.Create();
            var projects = await registry.GetProjectsAsync(ct);
            if (projects.Count == 0)
            {
                output.Info("No projects registered yet. Use 'add-project' to register one.");
                return 0;
            }

            foreach (var project in projects)
            {
                var localEnvs = project.LocalEnvironments.Count == 0
                    ? "(none)"
                    : string.Join(", ", project.LocalEnvironments.Select(e => e.Name));
                var remoteEnvs = project.RemoteEnvironments.Count == 0
                    ? "(none)"
                    : string.Join(", ", project.RemoteEnvironments.Select(e => e.Name));
                var csprojPath = string.IsNullOrWhiteSpace(project.CsprojPath) ? "(no csproj)" : project.CsprojPath;
                output.Info($"{project.Name}  ->  {csprojPath}  [{project.PubxmlName}]  local envs: {localEnvs}  dev-server envs: {remoteEnvs}");
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
