using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.IIS;
using PublishTool.Core.Models;
using PublishTool.Core.Services;
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

    var contentType = Path.GetExtension(fullPath).Equals(".txt", StringComparison.OrdinalIgnoreCase)
        ? "text/plain"
        : "application/zip";

    return Results.File(fullPath, contentType, Path.GetFileName(fullPath));
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

    var contentType = Path.GetExtension(fullPath).Equals(".txt", StringComparison.OrdinalIgnoreCase)
        ? "text/plain"
        : "application/zip";

    return Results.File(fullPath, contentType, Path.GetFileName(fullPath));
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

    var handler = new BuildUploadHandler();
    var result = await handler.HandleAsync(buildsRoot, new BuildUploadRequest(
        zipStream, zipFile.FileName,
        manifestStream,
        releaseNotesStream, releaseNotesFile?.FileName,
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
// /api/deploy -- extracts an already-uploaded build and makes it live on THIS server's own IIS,
// using the project's shared Remote* deploy target. Requires the app pool identity to have IIS
// management rights on this machine (see the plan's operational-requirement note) -- a real
// escalation from the read/write-BuildsRoot-only surface above.
// ---------------------------------------------------------------------------------------------

app.MapPost("/api/deploy", async (HttpRequest request, IConfiguration configuration, ILoggerFactory loggerFactory, string path) =>
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
    if (project is null || string.IsNullOrWhiteSpace(project.RemoteIisHostPath))
    {
        return Results.BadRequest(new { error = $"'{manifest.ProjectName}' has no dev-server IIS deploy target configured." });
    }

    var stagingDir = Path.Combine(Path.GetTempPath(), "PublishTool.Hosting", Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(stagingDir);
        ZipFile.ExtractToDirectory(manifest.ZipPath, stagingDir);

        var deployer = new BuildDeployer(new LoggerOutputSink(loggerFactory.CreateLogger("Deploy")));
        await deployer.DeployAsync(
            project.Name, project.RemoteIisHostPath!, project.RemoteIisBindings, project.RemoteAutoCreateIisSite,
            stagingDir, request.HttpContext.RequestAborted);

        return Results.Ok(new { deployed = true, project = project.Name, hostPath = project.RemoteIisHostPath });
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

app.MapGet("/api/iis/sites", async (HttpRequest request, IConfiguration configuration) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    try
    {
        return Results.Ok(await new IisSiteManager(NullOutputSink.Instance).ListSitesAsync(request.HttpContext.RequestAborted));
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
        return Results.Ok(await new IisSiteManager(NullOutputSink.Instance).ListAppPoolsAsync(request.HttpContext.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/iis/sites/{name}/start", async (HttpRequest request, IConfiguration configuration, string name) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    try
    {
        await new IisSiteManager(NullOutputSink.Instance).StartSiteAsync(name, request.HttpContext.RequestAborted);
        return Results.Ok();
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/iis/sites/{name}/stop", async (HttpRequest request, IConfiguration configuration, string name) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    try
    {
        await new IisSiteManager(NullOutputSink.Instance).StopSiteAsync(name, request.HttpContext.RequestAborted);
        return Results.Ok();
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/iis/apppools/{name}/start", async (HttpRequest request, IConfiguration configuration, string name) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    try
    {
        await new IisSiteManager(NullOutputSink.Instance).StartAppPoolAsync(name, request.HttpContext.RequestAborted);
        return Results.Ok();
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/iis/apppools/{name}/stop", async (HttpRequest request, IConfiguration configuration, string name) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    try
    {
        await new IisSiteManager(NullOutputSink.Instance).StopAppPoolAsync(name, request.HttpContext.RequestAborted);
        return Results.Ok();
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/iis/apppools/{name}/recycle", async (HttpRequest request, IConfiguration configuration, string name) =>
{
    if (!ApiKeyAuth.Validate(request, configuration))
    {
        return Results.Unauthorized();
    }

    try
    {
        await new IisSiteManager(NullOutputSink.Instance).RecycleAppPoolAsync(name, request.HttpContext.RequestAborted);
        return Results.Ok();
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
        FilterValue = sharedProject.EventLogFilterValue,
    };

#pragma warning disable CA1416 // EventLogReaderService is Windows-only; Hosting only ever runs on Windows despite its plain net8.0 TFM (same as the other pragma-suppressed CA1416 sites in this solution).
    var records = new EventLogReaderService().GetRecent(options);
#pragma warning restore CA1416
    return Results.Ok(records);
});

app.Run();
