using PublishTool.Core.Models;
using PublishTool.Core.Services.AppConfig;

namespace PublishTool.Core.Services;

public sealed class Publisher
{
    private readonly IProjectRegistry _registry;
    private readonly IOutputSink _output;
    private readonly MsBuildRunner _msBuild;
    private readonly BuildRepository _buildRepository;
    private readonly GitService _git;
    private readonly RemoteHostingClient _remoteHostingClient;
    private readonly BuildDeployer _buildDeployer;

    public Publisher(IProjectRegistry registry, IOutputSink output)
    {
        _registry = registry;
        _output = output;
        _msBuild = new MsBuildRunner(output);
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

        if (string.IsNullOrWhiteSpace(project.CsprojPath))
        {
            throw new InvalidOperationException(
                $"'{project.Name}' has no .csproj path configured -- set one in the project's Edit dialog before publishing.");
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
            await _git.CheckoutAsync(project.CsprojPath, options.GitBranch, ct);
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

            if (string.IsNullOrWhiteSpace(project.AppConfigPath))
            {
                throw new InvalidOperationException($"'{project.Name}' has app config enabled but no config file path set.");
            }

            _output.Stage($"Writing app config ({provider.DisplayName})...");
            await Task.Run(() => provider.WriteSettings(project.AppConfigPath, options.AppConfigSettings), ct);
        }

        var stagingDir = Path.Combine(Path.GetTempPath(), "PublishTool", Guid.NewGuid().ToString("N"));

        // Only populated in remote mode -- a throwaway location for the zip/manifest/release-notes
        // built purely to upload, never the shared local BuildsRoot (see PublishOptions.UseRemoteMode).
        string? uploadStagingDir = null;

        try
        {
            var msBuildExePath = await MsBuildLocator.LocateAsync(options.MsBuildPath, ct);
            _output.Info($"Using MSBuild at {msBuildExePath}");

            _output.Stage("Running MSBuild publish...");
            await _msBuild.PublishAsync(
                msBuildExePath, project.CsprojPath, project.PubxmlName, stagingDir,
                project.SdkStyleProject, project.ExtraPublishTargets, ct);

            string zipPath, manifestPath, releaseNotesPath;
            string? existingReleaseNotesReference;

            if (options.UseRemoteMode)
            {
                _output.Stage("Preparing build for upload to dev server...");
                uploadStagingDir = Path.Combine(Path.GetTempPath(), "PublishTool", Guid.NewGuid().ToString("N") + "-upload");
                Directory.CreateDirectory(uploadStagingDir);
                zipPath = Path.Combine(uploadStagingDir, $"{options.Version}.zip");
                manifestPath = Path.Combine(uploadStagingDir, $"{options.Version}.manifest.json");
                releaseNotesPath = Path.Combine(uploadStagingDir, $"{options.Version}.releasenotes.txt");

                await Task.Run(() => _buildRepository.WriteZip(zipPath, stagingDir), ct);
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

                    await Task.Run(() => _buildRepository.WriteZip(zipPath, stagingDir), ct);

                    // Overwriting a build that already had release notes reuses its reference number
                    // instead of minting a new one -- this is still the same release, just edited.
                    existingReleaseNotesReference = existing.Manifest.ReleaseNotesPath is { } existingNotesPath && File.Exists(existingNotesPath)
                        ? ReleaseNotesFormatter.Parse(File.ReadAllText(existingNotesPath))?.Reference
                        : null;
                }
                else
                {
                    _output.Stage("Archiving build to repository (zip)...");
                    // ZipFile.CreateFromDirectory is synchronous and can take real time on large
                    // builds (tens of MB, thousands of files) -- Task.Run keeps that off the UI thread.
                    var archive = await Task.Run(
                        () => _buildRepository.Archive(options.BuildsRoot, project.Name, options.Version, stagingDir), ct);
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
                ListInHosting = project.ListInHosting,
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

                var remoteEnvironment = options.DeployTarget == DeployTarget.Remote && project.RemoteIisEnabled && options.DeployEnvironmentName is not null
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

            var localEnvironment = options.DeployTarget == DeployTarget.Local && project.LocalIisEnabled && options.DeployEnvironmentName is not null
                ? project.LocalEnvironments.FirstOrDefault(e => string.Equals(e.Name, options.DeployEnvironmentName, StringComparison.OrdinalIgnoreCase))
                : null;

            if (localEnvironment is not null)
            {
                var localHostPath = localEnvironment.ResolveHostPath(project.Name);
                if (localHostPath is not null)
                {
                    var siteName = localEnvironment.ResolveSiteName(project.Name);
                    await _buildDeployer.DeployAsync(
                        siteName, localHostPath, localEnvironment.Bindings, localEnvironment.AutoCreateSite, stagingDir,
                        new SiteDeploymentRecord
                        {
                            SiteName = siteName,
                            ProjectName = project.Name,
                            Version = options.Version,
                            EnvironmentName = localEnvironment.Name,
                            DeployedAtUtc = DateTimeOffset.UtcNow,
                            DeployedBy = Environment.UserName,
                        },
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
