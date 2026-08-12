var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();

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

    return Results.File(fullPath, "application/zip", Path.GetFileName(fullPath));
});

app.Run();
