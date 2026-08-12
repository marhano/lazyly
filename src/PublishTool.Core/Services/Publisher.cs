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

    public Publisher(ProjectRegistry registry, IOutputSink output)
    {
        _registry = registry;
        _output = output;
        _msBuild = new MsBuildRunner(output);
        _mirror = new RobocopyMirror(output);
        _buildRepository = new BuildRepository();
        _iisSiteManager = new IisSiteManager(output);
        _git = new GitService(output);
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
            });

            _output.Info($"Archived to {zipPath}");

            if (project.AutoCreateIisSite)
            {
                _output.Stage("Ensuring IIS site exists...");
                await _iisSiteManager.EnsureSiteExistsAsync(project.Name, project.IisHostPath, project.IisBindings, ct);
            }

            _output.Stage($"Deploying to IIS host path: {project.IisHostPath}");
            await _mirror.MirrorAsync(stagingDir, project.IisHostPath, ct);

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
}
