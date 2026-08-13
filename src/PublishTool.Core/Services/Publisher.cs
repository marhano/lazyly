using PublishTool.Core.Models;
using PublishTool.Core.Services.AppConfig;

namespace PublishTool.Core.Services;

public sealed class Publisher
{
    private readonly ProjectRegistry _registry;
    private readonly IOutputSink _output;
    private readonly MsBuildRunner _msBuild;
    private readonly RobocopyMirror _mirror;
    private readonly BuildRepository _buildRepository;
    private readonly IisSiteManager _iisSiteManager;
    private readonly GitService _git;
    private readonly RemoteHostingClient _remoteHostingClient;

    public Publisher(ProjectRegistry registry, IOutputSink output)
    {
        _registry = registry;
        _output = output;
        _msBuild = new MsBuildRunner(output);
        _mirror = new RobocopyMirror(output);
        _buildRepository = new BuildRepository();
        _iisSiteManager = new IisSiteManager(output);
        _git = new GitService(output);
        _remoteHostingClient = new RemoteHostingClient();
    }

    public async Task<string> PublishAsync(PublishOptions options, CancellationToken ct = default)
    {
        var project = _registry.Get(options.ProjectName)
            ?? throw new InvalidOperationException(
                $"Project '{options.ProjectName}' is not registered. Add it first with 'add-project'.");

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

        try
        {
            var msBuildExePath = await MsBuildLocator.LocateAsync(options.MsBuildPath, ct);
            _output.Info($"Using MSBuild at {msBuildExePath}");

            _output.Stage("Running MSBuild publish...");
            await _msBuild.PublishAsync(
                msBuildExePath, project.CsprojPath, project.PubxmlName, stagingDir,
                project.SdkStyleProject, project.ExtraPublishTargets, ct);

            var existing = await Task.Run(
                () => _buildRepository.FindBuild(options.BuildsRoot, project.Name, options.Version), ct);

            string zipPath, manifestPath, releaseNotesPath;
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
            }

            string? writtenReleaseNotesPath = null;
            if (!string.IsNullOrWhiteSpace(project.ProjectId))
            {
                _output.Stage("Generating release notes...");

                // Overwriting a build that already had release notes reuses its reference number
                // instead of minting a new one -- this is still the same release, just edited.
                var existingReference = existing?.Manifest.ReleaseNotesPath is { } existingNotesPath && File.Exists(existingNotesPath)
                    ? ReleaseNotesFormatter.Parse(File.ReadAllText(existingNotesPath))?.Reference
                    : null;

                string reference;
                if (existingReference is not null)
                {
                    reference = existingReference;
                    _output.Info($"Reusing existing release notes reference: {reference}");
                }
                else
                {
                    var sequence = project.LastReleaseNotesSequence + 1;
                    reference = $"{project.ProjectId}-{DateTime.Now.Year}-{sequence:D4}";
                    project.LastReleaseNotesSequence = sequence;
                    _registry.AddOrUpdate(project);
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

            if (options.MarkAsLatest)
            {
                await Task.Run(() => _buildRepository.SetLatest(options.BuildsRoot, project.Name, manifestPath), ct);
                _output.Info($"Flagged {project.Name} v{options.Version} as the latest release.");
            }

            _output.Info($"Archived to {zipPath}");

            if (options.PublishToRemoteHosting)
            {
                if (string.IsNullOrWhiteSpace(options.RemoteHostingUrl))
                {
                    throw new InvalidOperationException(
                        "\"Also upload to remote hosting\" is checked, but no Remote Hosting URL is configured in Settings.");
                }

                _output.Stage("Uploading to remote hosting...");
                await _remoteHostingClient.UploadBuildAsync(
                    options.RemoteHostingUrl, options.RemoteHostingApiKey, zipPath, manifestPath, writtenReleaseNotesPath, ct);
                _output.Info("Uploaded to remote hosting.");
            }

            if (project.AutoCreateIisSite)
            {
                _output.Stage("Ensuring IIS site exists...");
                await _iisSiteManager.EnsureSiteExistsAsync(project.Name, project.IisHostPath, project.IisBindings, ct);
            }

            _output.Stage($"Deploying to IIS host path: {project.IisHostPath}");

            // A running IIS app pool for this site holds its DLLs open (in-process hosting keeps
            // them loaded in w3wp.exe), which makes robocopy fail to overwrite them with a sharing
            // violation -- stopping the pool first (best-effort; most projects aren't IIS-hosted at
            // all, so a missing pool is the normal case, not a problem) avoids that entirely instead
            // of relying on robocopy's slow 30-second retry loop.
            var appPoolWasStopped = await TryStopAppPoolForDeployAsync(project.Name, ct);
            try
            {
                await _mirror.MirrorAsync(stagingDir, project.IisHostPath, ct);
            }
            finally
            {
                if (appPoolWasStopped)
                {
                    await TryStartAppPoolAsync(project.Name, ct);
                }
            }

            _output.Stage("Publish complete.");
            _output.Notify($"{project.Name} published", $"Version {options.Version}", zipPath);
            return zipPath;
        }
        finally
        {
            if (Directory.Exists(stagingDir))
            {
                Directory.Delete(stagingDir, recursive: true);
            }
        }
    }

    /// <summary>Stops the IIS application pool sharing this project's name, if one exists, so its
    /// files can be overwritten. Swallows any failure silently (IIS not installed, no such pool,
    /// no permission) -- most projects aren't IIS-hosted at all, so that's the expected case, not
    /// something worth warning about on every publish.</summary>
    private async Task<bool> TryStopAppPoolForDeployAsync(string poolName, CancellationToken ct)
    {
        try
        {
            if (!await IisSiteManager.AppPoolExistsAsync(poolName, ct))
            {
                return false;
            }

            _output.Info($"Stopping IIS application pool '{poolName}' before copying files...");
            await _iisSiteManager.StopAppPoolAsync(poolName, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Restarts a pool this same publish stopped. Unlike the stop side, a failure here is
    /// worth surfacing -- it means the site was left down.</summary>
    private async Task TryStartAppPoolAsync(string poolName, CancellationToken ct)
    {
        try
        {
            await _iisSiteManager.StartAppPoolAsync(poolName, ct);
            _output.Info($"Restarted IIS application pool '{poolName}'.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _output.Warn($"Couldn't restart IIS application pool '{poolName}' after deploying -- start it manually. ({ex.Message})");
        }
    }
}
