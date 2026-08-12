using PublishTool.Core.Models;

namespace PublishTool.Core.Services;

public sealed class Publisher
{
    private readonly ProjectRegistry _registry;
    private readonly IOutputSink _output;
    private readonly MsBuildRunner _msBuild;
    private readonly RobocopyMirror _mirror;
    private readonly BuildRepository _buildRepository;
    private readonly IisSiteManager _iisSiteManager;

    public Publisher(ProjectRegistry registry, IOutputSink output)
    {
        _registry = registry;
        _output = output;
        _msBuild = new MsBuildRunner(output);
        _mirror = new RobocopyMirror(output);
        _buildRepository = new BuildRepository();
        _iisSiteManager = new IisSiteManager(output);
    }

    public async Task<string> PublishAsync(PublishOptions options, CancellationToken ct = default)
    {
        var project = _registry.Get(options.ProjectName)
            ?? throw new InvalidOperationException(
                $"Project '{options.ProjectName}' is not registered. Add it first with 'add-project'.");

        _output.Stage($"Publishing {project.Name} v{options.Version}...");

        if (!string.IsNullOrEmpty(project.AssemblyInfoPath))
        {
            _output.Stage("Stamping assembly version...");
            // File I/O is fast, but Task.Run keeps every synchronous step off the calling
            // thread consistently -- important when the caller is a WPF UI thread.
            await Task.Run(() => AssemblyVersionStamper.Stamp(project.AssemblyInfoPath, options.Version), ct);
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

            _output.Stage("Archiving build to repository (zip)...");
            // ZipFile.CreateFromDirectory is synchronous and can take real time on large
            // builds (tens of MB, thousands of files) -- Task.Run keeps that off the UI thread.
            var archive = await Task.Run(
                () => _buildRepository.Archive(options.BuildsRoot, project.Name, options.Version, stagingDir), ct);

            string? releaseNotesPath = null;
            if (!string.IsNullOrWhiteSpace(project.ProjectId))
            {
                _output.Stage("Generating release notes...");
                var sequence = project.LastReleaseNotesSequence + 1;
                var reference = $"{project.ProjectId}-{DateTime.Now.Year}-{sequence:D4}";
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

                await Task.Run(() => _buildRepository.WriteReleaseNotes(archive.ReleaseNotesPath, content), ct);
                releaseNotesPath = archive.ReleaseNotesPath;

                project.LastReleaseNotesSequence = sequence;
                _registry.AddOrUpdate(project);

                _output.Info($"Release notes reference: {reference}");
            }
            else
            {
                _output.Warn($"'{project.Name}' has no Project ID set -- skipping release notes generation.");
            }

            _buildRepository.WriteManifest(archive.ManifestPath, new BuildManifest
            {
                ProjectName = project.Name,
                Version = options.Version,
                PublishedAtUtc = DateTimeOffset.UtcNow,
                PublishedBy = Environment.UserName,
                ZipPath = archive.ZipPath,
                ListInHosting = project.ListInHosting,
                ReleaseNotesPath = releaseNotesPath,
            });

            _output.Info($"Archived to {archive.ZipPath}");

            if (project.AutoCreateIisSite)
            {
                _output.Stage("Ensuring IIS site exists...");
                await _iisSiteManager.EnsureSiteExistsAsync(project.Name, project.IisHostPath, project.IisBindings, ct);
            }

            _output.Stage($"Deploying to IIS host path: {project.IisHostPath}");
            await _mirror.MirrorAsync(stagingDir, project.IisHostPath, ct);

            _output.Stage("Publish complete.");
            _output.Notify($"{project.Name} published", $"Version {options.Version}", archive.ZipPath);
            return archive.ZipPath;
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
