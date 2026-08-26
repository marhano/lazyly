using System.Text.Json;
using PublishTool.Core.Models;

namespace PublishTool.Core.Services;

/// <summary>
/// Append-only deployment history for IIS sites, one JSON Lines file per site (one compact record
/// per line -- cheap to append without a read-modify-write, naturally ordered oldest-to-newest on
/// disk). Deliberately stored outside any site's own web root: IIS serves static files by default,
/// so a marker file living inside a deployed site would be readable by anyone who guessed its name.
/// </summary>
public sealed class SiteDeploymentStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Machine-wide, not tied to whichever Windows account happens to run PublishTool --
    /// IIS sites and app pools are machine-wide facts, and more than one admin may use this box.</summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PublishTool", "Deployments");

    public async Task AppendAsync(string deploymentsRoot, SiteDeploymentRecord record, CancellationToken ct = default)
    {
        Directory.CreateDirectory(deploymentsRoot);
        var path = ResolvePath(deploymentsRoot, record.SiteName);
        var line = JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine;
        await File.AppendAllTextAsync(path, line, ct);
    }

    /// <summary>Newest-first.</summary>
    public async Task<IReadOnlyList<SiteDeploymentRecord>> GetHistoryAsync(string deploymentsRoot, string siteName, CancellationToken ct = default)
    {
        var path = ResolvePath(deploymentsRoot, siteName);
        if (!File.Exists(path))
        {
            return Array.Empty<SiteDeploymentRecord>();
        }

        var lines = await File.ReadAllLinesAsync(path, ct);
        var records = new List<SiteDeploymentRecord>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var record = JsonSerializer.Deserialize<SiteDeploymentRecord>(line, JsonOptions);
            if (record is not null)
            {
                records.Add(record);
            }
        }

        records.Reverse();
        return records;
    }

    private static string ResolvePath(string deploymentsRoot, string siteName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var safeName = new string(siteName.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
        return Path.Combine(deploymentsRoot, $"{safeName}.jsonl");
    }
}
