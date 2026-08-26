using System.Text.Json;
using PublishTool.Core.Models;

namespace PublishTool.Core.Services;

/// <summary>
/// Append-only audit trail for the IIS tab -- same JSON Lines approach and single-global-log
/// reasoning as <see cref="FirewallAuditStore"/>.
/// </summary>
public sealed class IisAuditStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PublishTool", "IisAudit");

    private const string FileName = "audit.jsonl";

    public async Task AppendAsync(string auditRoot, IisAuditEntry entry, CancellationToken ct = default)
    {
        Directory.CreateDirectory(auditRoot);
        var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
        await File.AppendAllTextAsync(Path.Combine(auditRoot, FileName), line, ct);
    }

    /// <summary>Newest-first.</summary>
    public async Task<IReadOnlyList<IisAuditEntry>> GetHistoryAsync(string auditRoot, CancellationToken ct = default)
    {
        var path = Path.Combine(auditRoot, FileName);
        if (!File.Exists(path))
        {
            return Array.Empty<IisAuditEntry>();
        }

        var lines = await File.ReadAllLinesAsync(path, ct);
        var entries = new List<IisAuditEntry>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var entry = JsonSerializer.Deserialize<IisAuditEntry>(line, JsonOptions);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        entries.Reverse();
        return entries;
    }
}
