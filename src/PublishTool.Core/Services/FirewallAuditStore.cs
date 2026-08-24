using System.Text.Json;
using PublishTool.Core.Models;

namespace PublishTool.Core.Services;

/// <summary>
/// Append-only audit trail for the Firewall tab's Add/Edit/Remove actions -- same JSON Lines
/// approach as <see cref="SiteDeploymentStore"/>, but a single global log rather than one file
/// per key, since a removed rule still needs to stay visible in history.
/// </summary>
public sealed class FirewallAuditStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PublishTool", "FirewallAudit");

    private const string FileName = "audit.jsonl";

    public async Task AppendAsync(string auditRoot, FirewallAuditEntry entry, CancellationToken ct = default)
    {
        Directory.CreateDirectory(auditRoot);
        var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
        await File.AppendAllTextAsync(Path.Combine(auditRoot, FileName), line, ct);
    }

    /// <summary>Newest-first.</summary>
    public async Task<IReadOnlyList<FirewallAuditEntry>> GetHistoryAsync(string auditRoot, CancellationToken ct = default)
    {
        var path = Path.Combine(auditRoot, FileName);
        if (!File.Exists(path))
        {
            return Array.Empty<FirewallAuditEntry>();
        }

        var lines = await File.ReadAllLinesAsync(path, ct);
        var entries = new List<FirewallAuditEntry>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var entry = JsonSerializer.Deserialize<FirewallAuditEntry>(line, JsonOptions);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        entries.Reverse();
        return entries;
    }
}
