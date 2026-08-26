using PublishTool.Core.Models;
using PublishTool.Core.Services.AppConfig;
using PublishTool.Core.Services.BuildRunners;

namespace PublishTool.Core.Services;

public sealed class Publisher
{
    private readonly IProjectRegistry _registry;
    private readonly IOutputSink _output;
    private readonly BuildRepository _buildRepository;
    private readonly GitService _git;
    private readonly RemoteHostingClient _remoteHostingClient;
    private readonly BuildDeployer _buildDeployer;

    public Publisher(IProjectRegistry registry, IOutputSink output)
    {
        _registry = registry;
        _output = output;
        _buildRepository = new BuildRepository();
        _git = new GitService(output);
        _remoteHostingClient = new RemoteHostingClient();
        _buildDeployer = new BuildDeployer(output);
    }

    public async Task<string> PublishAsync(PublishOptions options, CancellationToken ct = default)
    {
        var project = await _registry.GetAsync(options.ProjectName, ct)
            ?? throw new InvalidOperationException(
                $"Project '{options.ProjectName}' is not registered. Add it first with 'add-project'.");

        var sourceRootPath = project.SourceRootPath;
        if (string.IsNullOrWhiteSpace(sourceRootPath))
        {
            throw new InvalidOperationException(
                $"'{project.Name}' has no project source configured -- set one in the project's Edit dialog before publishing.");
        }

        if (options.UseRemoteMode && string.IsNullOrWhiteSpace(options.RemoteHostingUrl))
        {
            throw new InvalidOperationException(
                $"'{project.Name}' can't be published -- \"Use dev server for projects\" is on, but no Remote " +
                "Build Hosting URL is configured in Settings. Configure one, or turn remote mode off to build locally.");
        }

        _output.Stage($"Publishing {project.Name} v{options.Version}...");

        if (!string.IsNullOrWhiteSpace(options.GitBranch))
        {
            await _git.CheckoutAsync(sourceRootPath, options.GitBranch, ct);
        }

        if (!string.IsNullOrEmpty(project.AssemblyInfoPath))
        {
            _output.Stage("Stamping assembly version...");
            // File I/O is fast, but Task.Run keeps every synchronous step off the calling
            // thread consistently -- important when the caller is a WPF UI thread.
            await Task.Run(() => AssemblyVersionStamper.Stamp(project.AssemblyInfoPath, options.Version), ct);
        }

        if (project.UseAppConfig && options.AppConfigSettings is { Count: > 0 })
        {
            var provider = AppConfigProviderRegistry.Get(project.AppConfigType)
                ?? throw new InvalidOperationException(
                    $"'{project.Name}' has an unknown app config type '{project.AppConfigType}'.");

            var configPath = ResolveAppConfigPath(project, options, provider);

            // Angular/Android no longer have a separate "build configuration" setting -- it's
            // inferred from whichever environment file app config is actually writing into (e.g.
            // environment.prod.ts -> "prod"), unless the caller already gave an explicit override.
            if (string.IsNullOrWhiteSpace(options.BuildConfiguration) && provider is EnvironmentTsProvider)
            {
                options.BuildConfiguration = EnvironmentTsProvider.InferBuildConfiguration(configPath);
            }

            _output.Stage($"Writing app config ({provider.DisplayName})...");
            await Task.Run(() => provider.WriteSettings(configPath, options.AppConfigSettings), ct);
        }

        if (project.ProjectType == ProjectType.Android && options.AndroidAppMetadata is not null
            && !string.IsNullOrWhiteSpace(project.Android?.ProjectRootPath))
        {
            var androidRootPath = project.Android.ProjectRootPath;
            var wrapper = AndroidWrapperStrategyRegistry.Detect(androidRootPath)
                ?? throw new InvalidOperationException(
                    $"'{project.Name}': couldn't detect a Capacitor or Cordova project at '{androidRootPath}' -- " +
                    "expected a capacitor.config.json/.ts or config.xml file there.");

            _output.Stage("Writing Android app config (bundle id / display name / version)...");
            await Task.Run(() => wrapper.WriteAppMetadata(androidRootPath, options.AndroidAppMetadata), ct);
        }

        var stagingDir = Path.Combine(Path.GetTempPath(), "PublishTool", Guid.NewGuid().ToString("N"));

        // Only populated in remote mode -- a throwaway location for the zip/manifest/release-notes
        // built purely to upload, never the shared local BuildsRoot (see PublishOptions.UseRemoteMode).
        string? uploadStagingDir = null;

        try
        {
            var runner = BuildRunnerRegistry.Get(project.ProjectType);
            var buildResult = await runner.BuildAsync(new BuildContext(project, options, stagingDir, _output), ct);

            // Copies buildResult's output to destinationPath -- zipping a Directory-kind result
            // (as every build did before Angular/Android existed), or copying a SingleFile-kind
            // result (an APK/AAB) as-is, since it's meant to be installed directly, not unzipped.
            Task WriteArtifactAsync(string destinationPath) => buildResult.ArtifactKind == BuildArtifactKind.Directory
                ? Task.Run(() => _buildRepository.WriteZip(destinationPath, buildResult.Path), ct)
                : Task.Run(() => File.Copy(buildResult.Path, destinationPath, overwrite: true), ct);

            string zipPath, manifestPath, releaseNotesPath;
            string? existingReleaseNotesReference;

            if (options.UseRemoteMode)
            {
                _output.Stage("Preparing build for upload to dev server...");
                var artifactExtension = buildResult.ArtifactKind == BuildArtifactKind.Directory ? ".zip" : Path.GetExtension(buildResult.Path);
                uploadStagingDir = Path.Combine(Path.GetTempPath(), "PublishTool", Guid.NewGuid().ToString("N") + "-upload");
                Directory.CreateDirectory(uploadStagingDir);
                zipPath = Path.Combine(uploadStagingDir, $"{options.Version}{artifactExtension}");
                manifestPath = Path.Combine(uploadStagingDir, $"{options.Version}.manifest.json");
                releaseNotesPath = Path.Combine(uploadStagingDir, $"{options.Version}.releasenotes.txt");

                await WriteArtifactAsync(zipPath);
                existingReleaseNotesReference = await TryGetExistingRemoteReleaseNotesReferenceAsync(options, project, ct);
            }
            else
            {
                var existing = await Task.Run(
                    () => _buildRepository.FindBuild(options.BuildsRoot, project.Name, options.Version), ct);

                if (existing is not null)
                {
                    _output.Stage($"Version {options.Version} already exists for {project.Name} -- overwriting in place...");
                    zipPath = existing.Manifest.ZipPath;
                    manifestPath = existing.ManifestPath;
                    // Pre-this-feature manifests may not have a release notes path yet -- fall back to
                    // the naming convention derived from the existing zip so an overwrite can still add one.
                    releaseNotesPath = existing.Manifest.ReleaseNotesPath
                        ?? Path.ChangeExtension(zipPath, null) + ".releasenotes.txt";

                    await WriteArtifactAsync(zipPath);

                    // Overwriting a build that already had release notes reuses its reference number
                    // instead of minting a new one -- this is still the same release, just edited.
                    existingReleaseNotesReference = existing.Manifest.ReleaseNotesPath is { } existingNotesPath && File.Exists(existingNotesPath)
                        ? ReleaseNotesFormatter.Parse(File.ReadAllText(existingNotesPath))?.Reference
                        : null;
                }
                else
                {
                    _output.Stage("Archiving build to repository...");
                    // ZipFile.CreateFromDirectory/File.Copy are synchronous and can take real time on
                    // large builds -- Task.Run keeps that off the UI thread.
                    var archive = buildResult.ArtifactKind == BuildArtifactKind.Directory
                        ? await Task.Run(() => _buildRepository.Archive(options.BuildsRoot, project.Name, options.Version, buildResult.Path), ct)
                        : await Task.Run(() => _buildRepository.ArchiveFile(options.BuildsRoot, project.Name, options.Version, buildResult.Path), ct);
                    zipPath = archive.ZipPath;
                    manifestPath = archive.ManifestPath;
                    releaseNotesPath = archive.ReleaseNotesPath;
                    existingReleaseNotesReference = null;
                }
            }

            string? writtenReleaseNotesPath = null;
            if (!string.IsNullOrWhiteSpace(project.ProjectId))
            {
                _output.Stage("Generating release notes...");

                string reference;
                if (existingReleaseNotesReference is not null)
                {
                    reference = existingReleaseNotesReference;
                    _output.Info($"Reusing existing release notes reference: {reference}");
                }
                else
                {
                    var sequence = await _registry.ReserveNextReleaseSequenceAsync(project.Name, ct);
                    reference = $"{project.ProjectId}-{DateTime.Now.Year}-{sequence:D4}";
                    _output.Info($"Release notes reference: {reference}");
                }

                var content = ReleaseNotesFormatter.Format(new ReleaseNotesEntry
                {
                    Reference = reference,
                    Version = options.Version,
                    Date = DateOnly.FromDateTime(DateTime.Now),
                    Features = options.ReleaseNotesFeatures,
                    Fixes = options.ReleaseNotesFixes,
                    OtherUpdates = options.ReleaseNotesOtherUpdates,
                    BacklogItems = options.ReleaseNotesBacklogItems,
                });

                await Task.Run(() => _buildRepository.WriteReleaseNotes(releaseNotesPath, content), ct);
                writtenReleaseNotesPath = releaseNotesPath;
            }
            else
            {
                _output.Warn($"'{project.Name}' has no Project ID set -- skipping release notes generation.");
            }

            _buildRepository.WriteManifest(manifestPath, new BuildManifest
            {
                ProjectName = project.Name,
                Version = options.Version,
                PublishedAtUtc = DateTimeOffset.UtcNow,
                PublishedBy = Environment.UserName,
                ZipPath = zipPath,
                ListInHosting = options.ListInHosting,
                ReleaseNotesPath = writtenReleaseNotesPath,
                AppConfigSettings = project.UseAppConfig ? options.AppConfigSettings : null,
                IsLatest = options.MarkAsLatest,
            });

            if (options.UseRemoteMode)
            {
                _output.Stage("Uploading to dev server...");
                var remoteManifestPath = await _remoteHostingClient.UploadBuildAsync(
                    options.RemoteHostingUrl!, options.RemoteHostingApiKey, zipPath, manifestPath, writtenReleaseNotesPath, ct);
                _output.Info("Uploaded to dev server.");
                // The uploaded manifest already carries IsLatest = options.MarkAsLatest -- the server
                // applies its own SetLatest from that when it accepts the upload, same as marking
                // latest locally does below, just server-side (see BuildUploadHandler).

                // IIS deploy only makes sense for a Directory-kind result (a folder of files to
                // serve) -- a SingleFile artifact (e.g. an Android APK/AAB) has no IIS deploy story
                // at all, so it's simply never offered as an option regardless of what's configured.
                var remoteEnvironment = buildResult.ArtifactKind == BuildArtifactKind.Directory
                    && options.DeployTarget == DeployTarget.Remote && project.RemoteIisEnabled && options.DeployEnvironmentName is not null
                    ? project.RemoteEnvironments.FirstOrDefault(e => string.Equals(e.Name, options.DeployEnvironmentName, StringComparison.OrdinalIgnoreCase))
                    : null;

                if (remoteEnvironment is not null)
                {
                    _output.Stage($"Deploying to dev server IIS ({remoteEnvironment.Name})...");
                    await _remoteHostingClient.DeployAsync(
                        options.RemoteHostingUrl!, options.RemoteHostingApiKey, remoteManifestPath, remoteEnvironment.Name, Environment.UserName, ct);
                    _output.Info($"Deployed to dev server IIS ({remoteEnvironment.Name}).");
                }
                else if (options.DeployTarget == DeployTarget.Remote && options.DeployEnvironmentName is not null)
                {
                    _output.Info($"'{project.Name}' has no dev-server deploy target named '{options.DeployEnvironmentName}' -- skipping deploy.");
                }
            }
            else
            {
                if (options.MarkAsLatest)
                {
                    await Task.Run(() => _buildRepository.SetLatest(options.BuildsRoot, project.Name, manifestPath), ct);
                    _output.Info($"Flagged {project.Name} v{options.Version} as the latest release.");
                }

                _output.Info($"Archived to {zipPath}");
            }

            var localEnvironment = buildResult.ArtifactKind == BuildArtifactKind.Directory
                && options.DeployTarget == DeployTarget.Local && project.LocalIisEnabled && options.DeployEnvironmentName is not null
                ? project.LocalEnvironments.FirstOrDefault(e => string.Equals(e.Name, options.DeployEnvironmentName, StringComparison.OrdinalIgnoreCase))
                : null;

            if (localEnvironment is not null)
            {
                var localHostPath = localEnvironment.ResolveHostPath(project.Name);
                if (localHostPath is not null)
                {
                    var siteName = localEnvironment.ResolveSiteName(project.Name);
                    await _buildDeployer.DeployAsync(
                        siteName, localHostPath, localEnvironment.Bindings, localEnvironment.AutoCreateSite, buildResult.Path,
                        new SiteDeploymentRecord
                        {
                            SiteName = siteName,
                            ProjectName = project.Name,
                            Version = options.Version,
                            EnvironmentName = localEnvironment.Name,
                            DeployedAtUtc = DateTimeOffset.UtcNow,
                            DeployedBy = Environment.UserName,
                        },
                        PoolTemplateFor(project.ProjectType),
                        ct);
                    _output.Info($"Deployed {project.Name} v{options.Version} to local IIS ({localEnvironment.Name}).");
                }
                else
                {
                    _output.Warn($"'{project.Name}' local environment '{localEnvironment.Name}' has no host root path configured -- skipping.");
                }
            }
            else if (options.DeployTarget == DeployTarget.Local && options.DeployEnvironmentName is not null)
            {
                _output.Info($"'{project.Name}' has no local deploy target named '{options.DeployEnvironmentName}' -- skipping local deploy.");
            }

            _output.Stage("Publish complete.");
            _output.Notify($"{project.Name} published", $"Version {options.Version}", options.UseRemoteMode ? null : zipPath);
            return zipPath;
        }
        finally
        {
            if (Directory.Exists(stagingDir))
            {
                Directory.Delete(stagingDir, recursive: true);
            }

            if (uploadStagingDir is not null && Directory.Exists(uploadStagingDir))
            {
                Directory.Delete(uploadStagingDir, recursive: true);
            }
        }
    }

    /// <summary>Angular's static-file output (like ASP.NET Core) wants a "No Managed Code" app
    /// pool, not the classic .NET Framework CLR default -- see AppPoolRuntimeTemplate.</summary>
    private static AppPoolRuntimeTemplate PoolTemplateFor(ProjectType projectType) =>
        projectType == ProjectType.Angular ? AppPoolRuntimeTemplate.NoManagedCode : AppPoolRuntimeTemplate.DotNetFramework;

    /// <summary>The config file path is optional even with app config enabled -- if neither the
    /// project's own <see cref="ProjectConfig.AppConfigPath"/> nor an explicit
    /// <see cref="PublishOptions.AppConfigPathOverride"/> (the GUI's Publish-tab pick, or the CLI's
    /// --app-config-path) is set, this searches the project's own source tree via
    /// <see cref="IAppConfigProvider.FindCandidateConfigPaths"/> and only proceeds if that search
    /// is unambiguous -- zero or multiple matches error out asking for an explicit choice instead
    /// of guessing.</summary>
    private static string ResolveAppConfigPath(ProjectConfig project, PublishOptions options, IAppConfigProvider provider)
    {
        if (!string.IsNullOrWhiteSpace(options.AppConfigPathOverride))
        {
            return options.AppConfigPathOverride;
        }

        if (!string.IsNullOrWhiteSpace(project.AppConfigPath))
        {
            return project.AppConfigPath;
        }

        var sourceRoot = string.IsNullOrWhiteSpace(project.SourceRootPath) ? null : Path.GetDirectoryName(project.SourceRootPath);
        var candidates = string.IsNullOrWhiteSpace(sourceRoot) ? [] : provider.FindCandidateConfigPaths(sourceRoot);

        return candidates.Count switch
        {
            1 => candidates[0],
            0 => throw new InvalidOperationException(
                $"'{project.Name}' has app config enabled but no config file path set, and none could be found " +
                $"automatically under its project folder for {provider.DisplayName}."),
            _ => throw new InvalidOperationException(
                $"'{project.Name}' has app config enabled but no config file path set, and multiple {provider.DisplayName} " +
                "files were found automatically -- pick one explicitly (Publish tab, or --app-config-path)."),
        };
    }

    /// <summary>Looks for an already-uploaded build of this exact version on the dev server so
    /// republishing it reuses its release notes reference instead of minting a new one -- the
    /// remote-mode counterpart to the local <see cref="BuildRepository.FindBuild"/> lookup. Resilient
    /// to failure (network hiccup, server briefly unreachable): worst case a republish gets a fresh
    /// reference number instead of reusing the old one, which is far better than blocking the whole
    /// publish over a nice-to-have lookup.</summary>
    private async Task<string?> TryGetExistingRemoteReleaseNotesReferenceAsync(PublishOptions options, ProjectConfig project, CancellationToken ct)
    {
        try
        {
            var builds = await _remoteHostingClient.ListBuildsAsync(options.RemoteHostingUrl!, options.RemoteHostingApiKey, project.Name, ct);
            var match = builds.FirstOrDefault(b => string.Equals(b.Version, options.Version, StringComparison.OrdinalIgnoreCase));
            if (match?.ReleaseNotesPath is null)
            {
                return null;
            }

            var tempNotesPath = Path.Combine(Path.GetTempPath(), "PublishTool", Guid.NewGuid().ToString("N") + ".releasenotes.txt");
            try
            {
                await _remoteHostingClient.DownloadAsync(options.RemoteHostingUrl!, options.RemoteHostingApiKey, match.ReleaseNotesPath, tempNotesPath, ct);
                return ReleaseNotesFormatter.Parse(await File.ReadAllTextAsync(tempNotesPath, ct))?.Reference;
            }
            finally
            {
                if (File.Exists(tempNotesPath))
                {
                    File.Delete(tempNotesPath);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _output.Warn($"Couldn't check the dev server for an existing release notes reference: {ex.Message}");
            return null;
        }
    }
}
