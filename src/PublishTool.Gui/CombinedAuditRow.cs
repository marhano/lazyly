namespace PublishTool.Gui;

/// <summary>
/// A display-only row unifying <c>ProjectAuditEntry</c>/<c>FirewallAuditEntry</c>/<c>IisAuditEntry</c>
/// into one shape for the "Audit Logs" tab -- each source keeps its own real model/store (see
/// ProjectAuditStore/FirewallAuditStore/IisAuditStore); this is purely a GUI-side merge for a single
/// combined view, not a new persisted format.
/// </summary>
public sealed class CombinedAuditRow
{
    /// <summary>"Project", "Firewall", or "IIS".</summary>
    public required string Category { get; set; }

    public required DateTimeOffset PerformedAtUtc { get; set; }

    public required string Action { get; set; }

    /// <summary>The project name, firewall rule name, or IIS site/pool name this entry is about.</summary>
    public required string Subject { get; set; }

    public string? Details { get; set; }

    public required string PerformedBy { get; set; }
}
