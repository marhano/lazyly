using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.IIS;

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

    var normalizedRoot = Path.GetFullPath(buildsRoot) + Path.DirectorySeparatorChar;
    var fullPath = Path.GetFullPath(Path.Combine(buildsRoot, path));

    // path comes from the client (a query string) -- reject anything that resolves outside
    // BuildsRoot (e.g. via "..") before touching the filesystem.
    if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
    {
        return Results.NotFound();
    }

    var contentType = Path.GetExtension(fullPath).Equals(".txt", StringComparison.OrdinalIgnoreCase)
        ? "text/plain"
        : "application/zip";

    return Results.File(fullPath, contentType, Path.GetFileName(fullPath));
});

app.Run();
