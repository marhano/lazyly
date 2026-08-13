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

app.Run();
