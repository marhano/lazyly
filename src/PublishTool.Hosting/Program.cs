using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.IIS;
using PublishTool.Core.Models;
using PublishTool.Core.Services;
using PublishTool.Core.Services.UnitTestRunners;
using PublishTool.Hosting;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();

// Uploaded builds can be sizeable (observed real-world builds run 60-100+ MB) -- raise the
// default 30 MB request-body limit at every layer that would otherwise reject them. The IIS
// Application Request Filtering limit (web.config's requestLimits) still applies on top of this
// when deployed under IIS and must be raised there too.
var maxUploadBytes = builder.Configuration.GetValue<long?>("MaxUploadBytes") ?? 524_288_000L; // 500 MB

builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = maxUploadBytes);
builder.Services.Configure<IISServerOptions>(options => options.MaxRequestBodySize = maxUploadBytes);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maxUploadBytes);

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapRazorPages();

app.MapGet("/download", (string path, IConfiguration configuration) =>
{
    var buildsRoot = configuration["BuildsRoot"];
    if (string.IsNullOrWhiteSpace(buildsRoot))
    {
        return Results.NotFound();
    }

    // path comes from the client (a query string) -- reject anything that resolves outside
    // BuildsRoot (e.g. via "..") before touching the filesystem.
    var fullPath = SafeBuildPath.Resolve(buildsRoot, path);
    if (fullPath is null || !File.Exists(fullPath))
    {
        return Results.NotFound();
    }

    return Results.File(fullPath, ContentTypeForDownload(fullPath), Path.GetFileName(fullPath));
});

// ---------------------------------------------------------------------------------------------
// /api/* -- for a detached PublishTool.Gui client with no filesystem access to this machine, not
// for the browser. Every route below requires ApiKeyAuth.Validate to pass, unlike everything
// above (which relies on network placement, same as today).
// ---------------------------------------------------------------------------------------------

app.MapGet("/api/ping", (HttpRequest request, IConfiguration configuration) =>
    ApiKeyAuth.Validate(request, configuration) ? Results.Ok(new { ok = true }) : Results.Unauthorized());

app.MapGet("/api/builds", (HttpRequest request, IConfiguration configuration, string? project) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    var buildsRoot = configuration["BuildsRoot"];
    if (string.IsNullOrWhiteSpace(buildsRoot) || !Directory.Exists(buildsRoot))
    {
        return Results.Problem("BuildsRoot isn't configured or accessible on this server.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var buildRepository = new BuildRepository();
    var builds = buildRepository.ListBuildsWithPaths(buildsRoot, project)
        .Select(b => BuildSummaryMapper.ToDto(buildsRoot, b.Manifest, b.ManifestPath))
        .ToList();

    return Results.Ok(builds);
});

app.MapGet("/api/builds/download", (HttpRequest request, IConfiguration configuration, string path) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    var buildsRoot = configuration["BuildsRoot"];
    if (string.IsNullOrWhiteSpace(buildsRoot))
    {
        return Results.NotFound();
    }

    var fullPath = SafeBuildPath.Resolve(buildsRoot, path);
    if (fullPath is null || !File.Exists(fullPath))
    {
        return Results.NotFound();
    }

    return Results.File(fullPath, ContentTypeForDownload(fullPath), Path.GetFileName(fullPath));
});

app.MapPost("/api/builds/upload", async (HttpRequest request, IConfiguration configuration) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    var buildsRoot = configuration["BuildsRoot"];
    if (string.IsNullOrWhiteSpace(buildsRoot) || !Directory.Exists(buildsRoot))
    {
        return Results.Problem("BuildsRoot isn't configured or accessible on this server.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { error = "Expected multipart/form-data." });
    }

    var form = await request.ReadFormAsync(request.HttpContext.RequestAborted);
    var zipFile = form.Files["BuildZip"];
    var manifestFile = form.Files["Manifest"];
    var releaseNotesFile = form.Files["ReleaseNotes"];
    var unitTestReportFile = form.Files["UnitTestReport"];
    var testBundleFile = form.Files["TestBundle"];

    if (zipFile is null || zipFile.Length == 0)
    {
        return Results.BadRequest(new { error = "BuildZip is required." });
    }

    if (manifestFile is null || manifestFile.Length == 0)
    {
        return Results.BadRequest(new { error = "Manifest is required." });
    }

    bool? markAsLatest = form.TryGetValue("MarkAsLatest", out var markLatestValues) &&
                          bool.TryParse(markLatestValues.ToString(), out var parsedMarkAsLatest)
        ? parsedMarkAsLatest
        : null;

    await using var zipStream = zipFile.OpenReadStream();
    await using var manifestStream = manifestFile.OpenReadStream();
    await using var releaseNotesStream = releaseNotesFile is { Length: > 0 } ? releaseNotesFile.OpenReadStream() : null;
    await using var unitTestReportStream = unitTestReportFile is { Length: > 0 } ? unitTestReportFile.OpenReadStream() : null;
    await using var testBundleStream = testBundleFile is { Length: > 0 } ? testBundleFile.OpenReadStream() : null;

    var handler = new BuildUploadHandler();
    var result = await handler.HandleAsync(buildsRoot, new BuildUploadRequest(
        zipStream, zipFile.FileName,
        manifestStream,
        releaseNotesStream, releaseNotesFile?.FileName,
        unitTestReportStream, unitTestReportFile?.FileName,
        testBundleStream, testBundleFile?.FileName,
        markAsLatest), request.HttpContext.RequestAborted);

    if (!result.Success)
    {
        return Results.BadRequest(new { error = result.ErrorMessage });
    }

    return Results.Created(
        $"/api/builds?project={Uri.EscapeDataString(result.ProjectName!)}",
        new { projectName = result.ProjectName, version = result.Version, manifestPath = SafeBuildPath.ToRelative(buildsRoot, result.ManifestPath!) });
});

app.MapMethods("/api/builds", new[] { "PATCH" }, async (HttpRequest request, IConfiguration configuration, string path) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    var buildsRoot = configuration["BuildsRoot"];
    if (string.IsNullOrWhiteSpace(buildsRoot))
    {
        return Results.NotFound();
    }

    var manifestPath = SafeBuildPath.Resolve(buildsRoot, path);
    if (manifestPath is null || !File.Exists(manifestPath))
    {
        return Results.NotFound();
    }

    var body = await request.ReadFromJsonAsync<UpdateBuildRequest>(request.HttpContext.RequestAborted);
    if (body is null)
    {
        return Results.BadRequest(new { error = "Expected a JSON body." });
    }

    // The manifest's own ProjectName is what SetLatest needs to know which sibling builds to
    // un-flag -- read it first rather than trusting anything from the client.
    var existingManifest = System.Text.Json.JsonSerializer.Deserialize<BuildManifest>(await File.ReadAllTextAsync(manifestPath));
    if (existingManifest is null)
    {
        return Results.Problem("That manifest exists but couldn't be read.", statusCode: StatusCodes.Status500InternalServerError);
    }

    var buildRepository = new BuildRepository();
    var updated = buildRepository.UpdateMetadata(buildsRoot, existingManifest.ProjectName, manifestPath, body.ListInHosting, body.IsLatest);
    if (updated is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(BuildSummaryMapper.ToDto(buildsRoot, updated, manifestPath));
});

app.MapDelete("/api/builds", (HttpRequest request, IConfiguration configuration, string path) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    var buildsRoot = configuration["BuildsRoot"];
    if (string.IsNullOrWhiteSpace(buildsRoot))
    {
        return Results.NotFound();
    }

    var manifestPath = SafeBuildPath.Resolve(buildsRoot, path);
    if (manifestPath is null || !File.Exists(manifestPath))
    {
        return Results.NotFound();
    }

    new BuildRepository().DeleteBuild(manifestPath);
    return Results.NoContent();
});

// ---------------------------------------------------------------------------------------------
// /api/projects -- the shared half of the project registry (see SharedProjectStore /
// RemoteProjectRegistry). Local-only fields never appear here; the GUI merges them in itself.
// ---------------------------------------------------------------------------------------------

app.MapGet("/api/projects", (HttpRequest request, IConfiguration configuration) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    var buildsRoot = configuration["BuildsRoot"];
    if (string.IsNullOrWhiteSpace(buildsRoot) || !Directory.Exists(buildsRoot))
    {
        return Results.Problem("BuildsRoot isn't configured or accessible on this server.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Ok(new SharedProjectStore().ListProjects(buildsRoot));
});

app.MapGet("/api/projects/{name}", (HttpRequest request, IConfiguration configuration, string name) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    var buildsRoot = configuration["BuildsRoot"];
    if (string.IsNullOrWhiteSpace(buildsRoot) || !UploadValidation.IsValidPathSegment(name))
    {
        return Results.NotFound();
    }

    var project = new SharedProjectStore().GetProject(buildsRoot, name);
    return project is null ? Results.NotFound() : Results.Ok(project);
});

app.MapPut("/api/projects/{name}", async (HttpRequest request, IConfiguration configuration, string name) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    var buildsRoot = configuration["BuildsRoot"];
    if (string.IsNullOrWhiteSpace(buildsRoot) || !Directory.Exists(buildsRoot))
    {
        return Results.Problem("BuildsRoot isn't configured or accessible on this server.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (!UploadValidation.IsValidPathSegment(name))
    {
        return Results.BadRequest(new { error = "Invalid project name." });
    }

    var body = await request.ReadFromJsonAsync<SharedProjectConfig>(request.HttpContext.RequestAborted);
    if (body is null)
    {
        return Results.BadRequest(new { error = "Expected a JSON body." });
    }

    if (!string.Equals(body.Name, name, StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "The project name in the body doesn't match the URL." });
    }

    var store = new SharedProjectStore();
    store.Upsert(buildsRoot, body);
    return Results.Ok(store.GetProject(buildsRoot, name));
});

app.MapDelete("/api/projects/{name}", (HttpRequest request, IConfiguration configuration, string name) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    var buildsRoot = configuration["BuildsRoot"];
    if (string.IsNullOrWhiteSpace(buildsRoot) || !UploadValidation.IsValidPathSegment(name))
    {
        return Results.NotFound();
    }

    return new SharedProjectStore().Delete(buildsRoot, name) ? Results.NoContent() : Results.NotFound();
});

app.MapPost("/api/projects/{name}/reserve-release-sequence", (HttpRequest request, IConfiguration configuration, string name) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    var buildsRoot = configuration["BuildsRoot"];
    if (string.IsNullOrWhiteSpace(buildsRoot) || !UploadValidation.IsValidPathSegment(name))
    {
        return Results.NotFound();
    }

    try
    {
        return Results.Ok(new { sequence = new SharedProjectStore().ReserveNextSequence(buildsRoot, name) });
    }
    catch (InvalidOperationException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
});

// ---------------------------------------------------------------------------------------------
// /api/projects/audit -- a flat log of every project-related action (add/remove/settings/publish/
// deploy/build changes) across every project, recorded by the client after an action succeeds
// (see MainWindow.RecordProjectAuditAsync). Same shape as /api/firewall/audit, just POST-able
// since (unlike a firewall rule change) there's no server-side mutation for this to piggyback on --
// the actual action already happened wherever it happened; this just logs that it did.
// ---------------------------------------------------------------------------------------------

app.MapGet("/api/projects/audit", async (HttpRequest request, IConfiguration configuration) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    try
    {
        var history = await new ProjectAuditStore().GetHistoryAsync(ProjectAuditRoot(configuration), request.HttpContext.RequestAborted);
        return Results.Ok(history);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/projects/audit", async (HttpRequest request, IConfiguration configuration) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    var body = await request.ReadFromJsonAsync<ProjectAuditEntry>(request.HttpContext.RequestAborted);
    if (body is null)
    {
        return Results.BadRequest(new { error = "Expected a JSON body." });
    }

    try
    {
        await new ProjectAuditStore().AppendAsync(ProjectAuditRoot(configuration), body, request.HttpContext.RequestAborted);
        return Results.Ok();
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

// ---------------------------------------------------------------------------------------------
// /api/tests/status -- the latest test run result per build (project name + version). When "Run
// Tests" executes locally (a dev's own machine, using their local TestSuites paths), the client
// already did the real work and this just records/serves the result, same "client already did the
// work, this just logs it" shape as /api/projects/audit, except this overwrites the one current
// status per build instead of appending to a log. /api/tests/run (below) is the other case -- the
// server does the work itself, for non-developer/remote-only users who have no local checkout at
// all -- and writes to this same store once it finishes.
// ---------------------------------------------------------------------------------------------

app.MapGet("/api/tests/status", async (HttpRequest request, IConfiguration configuration) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    try
    {
        var all = await new TestRunStatusStore().LoadAllAsync(TestRunStatusRoot(configuration), request.HttpContext.RequestAborted);
        return Results.Ok(all);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/tests/status", async (HttpRequest request, IConfiguration configuration) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    var body = await request.ReadFromJsonAsync<TestRunStatus>(request.HttpContext.RequestAborted);
    if (body is null)
    {
        return Results.BadRequest(new { error = "Expected a JSON body." });
    }

    try
    {
        await new TestRunStatusStore().SetAsync(TestRunStatusRoot(configuration), body, request.HttpContext.RequestAborted);
        return Results.Ok();
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

// /api/tests/run -- runs one build's own BUNDLED test suites ON THIS SERVER (see
// BuildManifest.TestBundlePath -- built/published by Publisher on the publishing dev's own machine
// and uploaded alongside the build, so there's no git/checkout/source needed here at all), streaming
// output live into the HTTP response body line-by-line as it happens (read incrementally by
// RemoteHostingClient.RunTestsAsync, same "live, not just the end result" requirement local runs
// already meet) -- this is what actually lets a non-developer, remote-only user trigger a real test
// run themselves, with no source or test tooling of their own. Requires this server machine to have
// whatever each suite's project type needs installed on it too: .NET SDK always (a modern suite only
// needs this -- dotnet vstest, no Visual Studio); Visual Studio/Build Tools (vstest.console.exe) for a
// classic packages.config suite; Playwright's browsers (a one-time "playwright.ps1 install" here) for
// an E2E suite -- same prerequisites a dev's own machine needs for local execution, just installed
// here instead.
app.MapPost("/api/tests/run", async (HttpRequest request, HttpResponse response, IConfiguration configuration, string project, string version, string? performedBy) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    var buildsRoot = configuration["BuildsRoot"];
    if (string.IsNullOrWhiteSpace(buildsRoot) || !Directory.Exists(buildsRoot))
    {
        return Results.Problem("BuildsRoot isn't configured or accessible on this server.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    // Past this point the response IS the log stream -- every failure from here on (including "no
    // such build" or "this build has no test bundle") is written into it as a line (via
    // HttpStreamOutputSink) rather than returned as a normal error IResult, since headers are about
    // to be committed as soon as the first line is written.
    response.StatusCode = StatusCodes.Status200OK;
    response.ContentType = "text/plain; charset=utf-8";

    // Kestrel disallows synchronous body writes by default (throws InvalidOperationException:
    // "Synchronous operations are disallowed") -- HttpStreamOutputSink's Write/Flush calls are
    // synchronous on purpose, to match IOutputSink's own synchronous contract without needing async
    // plumbing through every caller in this codebase. This is the standard, documented escape hatch
    // for exactly that case, scoped to just this one request.
    var syncIoFeature = request.HttpContext.Features.Get<IHttpBodyControlFeature>();
    if (syncIoFeature is not null)
    {
        syncIoFeature.AllowSynchronousIO = true;
    }

    var ct = request.HttpContext.RequestAborted;
    await using var writer = new StreamWriter(response.Body) { AutoFlush = true };
    var output = new HttpStreamOutputSink(writer);

    var workingDir = Path.Combine(Path.GetTempPath(), "PublishToolHosting", "tests", Guid.NewGuid().ToString("N"));
    UnitTestOrchestratorResult? result = null;
    try
    {
        output.Stage($"Running tests for '{project}' v{version} on the dev server...");
        result = await new RemoteTestRunnerService().RunAsync(buildsRoot, project, version, workingDir, output, ct);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        output.Error(ex.Message);
    }
    finally
    {
        if (Directory.Exists(workingDir))
        {
            try
            {
                Directory.Delete(workingDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup -- a locked file (e.g. an antivirus scan) here shouldn't
                // fail an otherwise-successful run.
            }
        }
    }

    if (result is null)
    {
        output.Warn($"'{project}' has no runnable test suites.");
        return Results.Empty;
    }

    output.Info(result.Passed ? "All tests passed." : "Tests failed.");

    try
    {
        var status = new TestRunStatus
        {
            ProjectName = project,
            Version = version,
            Passed = result.Passed,
            RunAtUtc = DateTimeOffset.UtcNow,
            PerformedBy = string.IsNullOrWhiteSpace(performedBy) ? "(unknown)" : performedBy,
            Suites = result.Suites
                .Select(s => new TestSuiteRunStatus
                {
                    SuiteName = s.SuiteName,
                    Passed = s.Passed,
                    TotalCount = s.TotalCount,
                    PassedCount = s.PassedCount,
                    FailedCount = s.FailedCount,
                    SkippedCount = s.SkippedCount,
                })
                .ToList(),
        };
        await new TestRunStatusStore().SetAsync(TestRunStatusRoot(configuration), status, ct);
    }
    catch (Exception ex)
    {
        output.Warn($"Tests ran, but the result couldn't be recorded: {ex.Message}");
    }

    return Results.Empty;
});

// ---------------------------------------------------------------------------------------------
// /api/environments -- the shared deployment environment name list (Staging/Production/etc.) every
// PublishTool user picks from when configuring or deploying to a project's environments.
// ---------------------------------------------------------------------------------------------

app.MapGet("/api/environments", (HttpRequest request, IConfiguration configuration) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    var buildsRoot = configuration["BuildsRoot"];
    if (string.IsNullOrWhiteSpace(buildsRoot) || !Directory.Exists(buildsRoot))
    {
        return Results.Problem("BuildsRoot isn't configured or accessible on this server.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Ok(new EnvironmentStore().Get(buildsRoot));
});

app.MapPut("/api/environments", async (HttpRequest request, IConfiguration configuration) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    var buildsRoot = configuration["BuildsRoot"];
    if (string.IsNullOrWhiteSpace(buildsRoot) || !Directory.Exists(buildsRoot))
    {
        return Results.Problem("BuildsRoot isn't configured or accessible on this server.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var body = await request.ReadFromJsonAsync<EnvironmentSettings>(request.HttpContext.RequestAborted);
    if (body is null)
    {
        return Results.BadRequest(new { error = "Expected a JSON body." });
    }

    new EnvironmentStore().Save(buildsRoot, body);
    return Results.Ok(body);
});

// ---------------------------------------------------------------------------------------------
// /api/deploy -- extracts an already-uploaded build and makes it live on THIS server's own IIS,
// using the project's shared Remote* deploy target. Requires the app pool identity to have IIS
// management rights on this machine (see the plan's operational-requirement note) -- a real
// escalation from the read/write-BuildsRoot-only surface above.
// ---------------------------------------------------------------------------------------------

app.MapPost("/api/deploy", async (HttpRequest request, IConfiguration configuration, ILoggerFactory loggerFactory, string path, string environment, string? deployedBy) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    var buildsRoot = configuration["BuildsRoot"];
    if (string.IsNullOrWhiteSpace(buildsRoot))
    {
        return Results.NotFound();
    }

    var manifestPath = SafeBuildPath.Resolve(buildsRoot, path);
    if (manifestPath is null || !File.Exists(manifestPath))
    {
        return Results.NotFound();
    }

    var manifest = JsonSerializer.Deserialize<BuildManifest>(await File.ReadAllTextAsync(manifestPath, request.HttpContext.RequestAborted));
    if (manifest is null)
    {
        return Results.Problem("That manifest exists but couldn't be read.", statusCode: StatusCodes.Status500InternalServerError);
    }

    if (!File.Exists(manifest.ZipPath))
    {
        return Results.Problem("That build's zip file is missing on the server.", statusCode: StatusCodes.Status500InternalServerError);
    }

    var project = new SharedProjectStore().GetProject(buildsRoot, manifest.ProjectName);
    var deployEnvironment = project?.RemoteEnvironments.FirstOrDefault(e => string.Equals(e.Name, environment, StringComparison.OrdinalIgnoreCase));
    var hostPath = deployEnvironment?.ResolveHostPath(manifest.ProjectName);
    if (deployEnvironment is null || hostPath is null)
    {
        return Results.BadRequest(new { error = $"'{manifest.ProjectName}' has no '{environment}' dev-server deploy target configured." });
    }

    var stagingDir = Path.Combine(Path.GetTempPath(), "PublishTool.Hosting", Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(stagingDir);
        ZipFile.ExtractToDirectory(manifest.ZipPath, stagingDir);

        var siteName = deployEnvironment.ResolveSiteName(manifest.ProjectName);
        var deployer = new BuildDeployer(
            new LoggerOutputSink(loggerFactory.CreateLogger("Deploy")), Path.Combine(buildsRoot, "_deployments"));
        var poolTemplate = project?.ProjectType == ProjectType.Angular
            ? AppPoolRuntimeTemplate.NoManagedCode
            : AppPoolRuntimeTemplate.DotNetFramework;
        await deployer.DeployAsync(
            siteName, hostPath, deployEnvironment.Bindings, deployEnvironment.AutoCreateSite, stagingDir,
            new SiteDeploymentRecord
            {
                SiteName = siteName,
                ProjectName = manifest.ProjectName,
                Version = manifest.Version,
                EnvironmentName = deployEnvironment.Name,
                DeployedAtUtc = DateTimeOffset.UtcNow,
                DeployedBy = string.IsNullOrWhiteSpace(deployedBy) ? "unknown" : deployedBy,
            },
            poolTemplate,
            request.HttpContext.RequestAborted);

        return Results.Ok(new { deployed = true, project = manifest.ProjectName, environment = deployEnvironment.Name, hostPath });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
    finally
    {
        if (Directory.Exists(stagingDir))
        {
            Directory.Delete(stagingDir, recursive: true);
        }
    }
});

// ---------------------------------------------------------------------------------------------
// /api/iis/* -- Hosting manages its OWN machine's IIS (it's running on the dev server), same as
// the local IIS tab already does via IisSiteManager -- just exposed over HTTP instead of appcmd
// run directly. Same operational requirement as /api/deploy above.
// ---------------------------------------------------------------------------------------------

// Deployment/audit history live under BuildsRoot (already configured, no new setting needed)
// rather than each manager's own machine-wide default -- this server may run other things too,
// and these records are specific to what PublishTool itself put into IIS/the firewall here.
// Every archived build is a .zip except Android's, which is a raw .apk/.aab (see BuildRepository.ArchiveFile) --
// both are also, technically, zip-format containers, so a wrong-but-plausible "application/zip" wouldn't
// actually break a download, but this gets the MIME type right for a browser/download manager either way.
static string ContentTypeForDownload(string filePath) => Path.GetExtension(filePath).ToLowerInvariant() switch
{
    ".txt" => "text/plain",
    ".apk" => "application/vnd.android.package-archive",
    ".aab" => "application/octet-stream",
    ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
    _ => "application/zip",
};

static string? DeploymentsRoot(IConfiguration configuration) =>
    configuration["BuildsRoot"] is { Length: > 0 } buildsRoot ? Path.Combine(buildsRoot, "_deployments") : null;

static string? FirewallAuditRoot(IConfiguration configuration) =>
    configuration["BuildsRoot"] is { Length: > 0 } buildsRoot ? Path.Combine(buildsRoot, "_firewall-audit") : null;

static string ProjectAuditRoot(IConfiguration configuration) =>
    configuration["BuildsRoot"] is { Length: > 0 } buildsRoot ? Path.Combine(buildsRoot, "_project-audit") : ProjectAuditStore.DefaultRoot;

static string IisAuditRoot(IConfiguration configuration) =>
    configuration["BuildsRoot"] is { Length: > 0 } buildsRoot ? Path.Combine(buildsRoot, "_iis-audit") : IisAuditStore.DefaultRoot;

static string TestRunStatusRoot(IConfiguration configuration) =>
    configuration["BuildsRoot"] is { Length: > 0 } buildsRoot ? Path.Combine(buildsRoot, "_test-status") : TestRunStatusStore.DefaultRoot;

static IisSiteManager CreateIisSiteManager(IConfiguration configuration) =>
    new(NullOutputSink.Instance, DeploymentsRoot(configuration), IisAuditRoot(configuration));

static string ResolvePerformedBy(string? performedBy) => string.IsNullOrWhiteSpace(performedBy) ? "unknown" : performedBy;

app.MapGet("/api/iis/sites", async (HttpRequest request, IConfiguration configuration) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    try
    {
        return Results.Ok(await CreateIisSiteManager(configuration).ListSitesAsync(request.HttpContext.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/api/iis/sites/{name}/history", async (HttpRequest request, IConfiguration configuration, string name) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    try
    {
        var history = await CreateIisSiteManager(configuration).GetDeploymentHistoryAsync(name, request.HttpContext.RequestAborted);
        return Results.Ok(history);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/api/iis/apppools", async (HttpRequest request, IConfiguration configuration) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    try
    {
        return Results.Ok(await CreateIisSiteManager(configuration).ListAppPoolsAsync(request.HttpContext.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/api/iis/audit", async (HttpRequest request, IConfiguration configuration) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    try
    {
        return Results.Ok(await CreateIisSiteManager(configuration).GetAuditHistoryAsync(request.HttpContext.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/iis/sites/{name}/start", async (HttpRequest request, IConfiguration configuration, string name, string? performedBy) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    try
    {
        await CreateIisSiteManager(configuration).StartSiteAsync(name, ResolvePerformedBy(performedBy), request.HttpContext.RequestAborted);
        return Results.Ok();
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/iis/sites/{name}/stop", async (HttpRequest request, IConfiguration configuration, string name, string? performedBy) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    try
    {
        await CreateIisSiteManager(configuration).StopSiteAsync(name, ResolvePerformedBy(performedBy), request.HttpContext.RequestAborted);
        return Results.Ok();
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapDelete("/api/iis/sites/{name}", async (HttpRequest request, IConfiguration configuration, string name, string? performedBy) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    try
    {
        await CreateIisSiteManager(configuration).DeleteSiteAsync(name, ResolvePerformedBy(performedBy), request.HttpContext.RequestAborted);
        return Results.Ok();
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/iis/apppools/{name}/start", async (HttpRequest request, IConfiguration configuration, string name, string? performedBy) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    try
    {
        await CreateIisSiteManager(configuration).StartAppPoolAsync(name, ResolvePerformedBy(performedBy), request.HttpContext.RequestAborted);
        return Results.Ok();
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/iis/apppools/{name}/stop", async (HttpRequest request, IConfiguration configuration, string name, string? performedBy) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    try
    {
        await CreateIisSiteManager(configuration).StopAppPoolAsync(name, ResolvePerformedBy(performedBy), request.HttpContext.RequestAborted);
        return Results.Ok();
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/iis/apppools/{name}/recycle", async (HttpRequest request, IConfiguration configuration, string name, string? performedBy) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    try
    {
        await CreateIisSiteManager(configuration).RecycleAppPoolAsync(name, ResolvePerformedBy(performedBy), request.HttpContext.RequestAborted);
        return Results.Ok();
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

// Deliberately re-validates identityType server-side against the same allow-list the enum already
// restricts to (rather than trusting the client) -- this is the one /api/iis/* action that grants a
// site elevated Windows privileges rather than just starting/stopping/recycling/removing it, so it
// gets a clear 400 for anything outside the allow-list instead of silently doing whatever appcmd
// would do with an unexpected value.
app.MapPost("/api/iis/apppools/{name}/identity", async (HttpRequest request, IConfiguration configuration, string name, string identityType, string? performedBy) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    if (!Enum.TryParse<AppPoolIdentityType>(identityType, ignoreCase: true, out var parsedIdentityType))
    {
        return Results.BadRequest($"Unknown identity type '{identityType}'. Allowed: {string.Join(", ", Enum.GetNames<AppPoolIdentityType>())}.");
    }

    try
    {
        await CreateIisSiteManager(configuration).SetAppPoolIdentityAsync(name, parsedIdentityType, ResolvePerformedBy(performedBy), request.HttpContext.RequestAborted);
        return Results.Ok();
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

// Uploads a zip and deploys it into a site on the dev server's own IIS, creating the site (and its
// own app pool) first if requested -- the remote counterpart to a local manual deploy, which calls
// BuildDeployer directly instead of needing to ship the source content anywhere.
app.MapPost("/api/iis/manual-deploy", async (HttpRequest request, IConfiguration configuration) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { error = "Expected multipart/form-data." });
    }

    var form = await request.ReadFormAsync(request.HttpContext.RequestAborted);
    var zipFile = form.Files["Zip"];
    if (zipFile is null || zipFile.Length == 0)
    {
        return Results.BadRequest(new { error = "Zip is required." });
    }

    var siteName = form["SiteName"].ToString();
    var physicalPath = form["PhysicalPath"].ToString();
    if (string.IsNullOrWhiteSpace(siteName) || string.IsNullOrWhiteSpace(physicalPath))
    {
        return Results.BadRequest(new { error = "SiteName and PhysicalPath are required." });
    }

    var autoCreateSite = bool.TryParse(form["AutoCreateSite"].ToString(), out var parsedAutoCreate) && parsedAutoCreate;
    var bindingsJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    var bindings = form["BindingsJson"].ToString() is { Length: > 0 } bindingsJson
        ? JsonSerializer.Deserialize<List<IisBinding>>(bindingsJson, bindingsJsonOptions) ?? new List<IisBinding>()
        : new List<IisBinding>();
    var poolTemplate = Enum.TryParse<AppPoolRuntimeTemplate>(form["PoolTemplate"].ToString(), out var parsedTemplate)
        ? parsedTemplate
        : AppPoolRuntimeTemplate.DotNetFramework;
    var label = form["Label"].ToString() is { Length: > 0 } formLabel ? formLabel : "manual";
    var performedBy = ResolvePerformedBy(form["PerformedBy"].ToString());

    var tempDir = Path.Combine(Path.GetTempPath(), "PublishTool.Hosting", Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(tempDir);
        var zipPath = Path.Combine(tempDir, "upload.zip");
        await using (var fileStream = File.Create(zipPath))
        {
            await zipFile.CopyToAsync(fileStream, request.HttpContext.RequestAborted);
        }

        var extractDir = Path.Combine(tempDir, "extracted");
        ZipFile.ExtractToDirectory(zipPath, extractDir);

        var deployer = new BuildDeployer(NullOutputSink.Instance, DeploymentsRoot(configuration));
        await deployer.DeployAsync(
            siteName, physicalPath, bindings, autoCreateSite, extractDir,
            new SiteDeploymentRecord
            {
                SiteName = siteName,
                ProjectName = "(manual)",
                Version = label,
                EnvironmentName = "(manual)",
                DeployedAtUtc = DateTimeOffset.UtcNow,
                DeployedBy = performedBy,
            },
            poolTemplate,
            request.HttpContext.RequestAborted);

        await new IisAuditStore().AppendAsync(IisAuditRoot(configuration), new IisAuditEntry
        {
            EntityType = "Site",
            EntityName = siteName,
            Action = "Manual Deploy",
            Details = label,
            PerformedAtUtc = DateTimeOffset.UtcNow,
            PerformedBy = performedBy,
        }, request.HttpContext.RequestAborted);

        return Results.Ok();
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
    finally
    {
        if (Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
});

// ---------------------------------------------------------------------------------------------
// /api/firewall/* -- Hosting manages its OWN machine's inbound Windows Firewall rules for ports
// IIS sites use here, same operational requirement (elevated app pool identity) as /api/iis/*.
// Only ever lists/manages rules PublishTool itself created -- see FirewallManager.
// ---------------------------------------------------------------------------------------------

app.MapGet("/api/firewall/rules", async (HttpRequest request, IConfiguration configuration, bool? all) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    try
    {
        var manager = new FirewallManager(NullOutputSink.Instance, FirewallAuditRoot(configuration));
        return Results.Ok(await manager.ListRulesAsync(all ?? false, request.HttpContext.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/firewall/rules", async (HttpRequest request, IConfiguration configuration) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    var body = await request.ReadFromJsonAsync<AddFirewallRuleRequest>(request.HttpContext.RequestAborted);
    if (body is null || string.IsNullOrWhiteSpace(body.Label) || string.IsNullOrWhiteSpace(body.Ports))
    {
        return Results.BadRequest(new { error = "Expected a JSON body with Label, Ports, Protocol, and PerformedBy." });
    }

    try
    {
        var manager = new FirewallManager(NullOutputSink.Instance, FirewallAuditRoot(configuration));
        await manager.AddInboundRuleAsync(body.Label, body.Ports, body.Protocol, body.PerformedBy, request.HttpContext.RequestAborted);
        return Results.Ok();
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPut("/api/firewall/rules", async (HttpRequest request, IConfiguration configuration) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    var body = await request.ReadFromJsonAsync<EditFirewallRuleRequest>(request.HttpContext.RequestAborted);
    if (body is null || string.IsNullOrWhiteSpace(body.CurrentName) || string.IsNullOrWhiteSpace(body.NewLabel) || string.IsNullOrWhiteSpace(body.Ports))
    {
        return Results.BadRequest(new { error = "Expected a JSON body with CurrentName, NewLabel, Ports, Protocol, and PerformedBy." });
    }

    try
    {
        var manager = new FirewallManager(NullOutputSink.Instance, FirewallAuditRoot(configuration));
        await manager.EditRuleAsync(body.CurrentName, body.NewLabel, body.Ports, body.Protocol, body.PerformedBy, request.HttpContext.RequestAborted);
        return Results.Ok();
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapDelete("/api/firewall/rules", async (HttpRequest request, IConfiguration configuration, string name, string? performedBy) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    try
    {
        var manager = new FirewallManager(NullOutputSink.Instance, FirewallAuditRoot(configuration));
        await manager.DeleteRuleAsync(name, string.IsNullOrWhiteSpace(performedBy) ? "unknown" : performedBy, request.HttpContext.RequestAborted);
        return Results.Ok();
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/api/firewall/audit", async (HttpRequest request, IConfiguration configuration) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    try
    {
        var manager = new FirewallManager(NullOutputSink.Instance, FirewallAuditRoot(configuration));
        return Results.Ok(await manager.GetAuditHistoryAsync(request.HttpContext.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

// ---------------------------------------------------------------------------------------------
// /api/eventlog -- Hosting reads its OWN local Windows Event Log using the project's shared
// EventLog* settings, so the GUI never needs direct EventLogSession access to this server (which
// would need Windows Remote Event Log Management firewall access, not just plain HTTPS).
// ---------------------------------------------------------------------------------------------

app.MapGet("/api/eventlog", (HttpRequest request, IConfiguration configuration, string project) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    var buildsRoot = configuration["BuildsRoot"];
    if (string.IsNullOrWhiteSpace(buildsRoot) || !UploadValidation.IsValidPathSegment(project))
    {
        return Results.NotFound();
    }

    var sharedProject = new SharedProjectStore().GetProject(buildsRoot, project);
    if (sharedProject is null)
    {
        return Results.NotFound();
    }

    if (!sharedProject.UseEventLog)
    {
        return Results.BadRequest(new { error = $"Event Logs isn't enabled for '{project}'." });
    }

    // Deliberately MachineName = null -- Hosting always reads its own local log, never proxies to
    // some other third machine, regardless of what the project's EventLogMachineName says (that
    // field is for the OTHER, direct-EventLogSession remote-reading path the GUI still has locally).
    var options = new EventLogQueryOptions
    {
        LogName = string.IsNullOrWhiteSpace(sharedProject.EventLogName) ? "Application" : sharedProject.EventLogName,
        MachineName = null,
        FilterType = sharedProject.EventLogFilterType ?? EventLogFilterTypes.Source,
        FilterValues = sharedProject.EventLogFilterValues.Count > 0
            ? sharedProject.EventLogFilterValues
            : string.IsNullOrWhiteSpace(sharedProject.EventLogFilterValue) ? Array.Empty<string>() : new[] { sharedProject.EventLogFilterValue },
    };

#pragma warning disable CA1416 // EventLogReaderService is Windows-only; Hosting only ever runs on Windows despite its plain net8.0 TFM (same as the other pragma-suppressed CA1416 sites in this solution).
    var records = new EventLogReaderService().GetRecent(options);
#pragma warning restore CA1416
    return Results.Ok(records);
});

app.Run();

internal sealed record AddFirewallRuleRequest(string Label, string Ports, string Protocol, string PerformedBy);

internal sealed record EditFirewallRuleRequest(string CurrentName, string NewLabel, string Ports, string Protocol, string PerformedBy);
