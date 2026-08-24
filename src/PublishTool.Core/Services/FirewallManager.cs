using System.Text.RegularExpressions;
using PublishTool.Core.Models;

namespace PublishTool.Core.Services;

/// <summary>
/// Manages inbound Windows Firewall rules via netsh.exe, same shell-out pattern as
/// <see cref="IisSiteManager"/> uses for appcmd.exe -- netsh is always on PATH (unlike appcmd),
/// so no hardcoded install-path lookup is needed. By default only ever lists/manages rules
/// PublishTool itself created (identified by <see cref="RuleNamePrefix"/>), never the hundreds of
/// unrelated rules already on a typical Windows machine -- this isn't a general firewall console,
/// though <see cref="ListRulesAsync"/> can optionally show everything for visibility.
/// </summary>
public sealed partial class FirewallManager
{
    private const string NetshPath = "netsh.exe";

    /// <summary>Every rule name PublishTool creates starts with this -- exposed publicly so the
    /// GUI can guard Edit/Remove against rules that don't (relevant once "Show all rules" is on)
    /// and strip it back off to recover the label for editing.</summary>
    public const string RuleNamePrefix = "[IIS] ";

    private readonly IOutputSink _output;
    private readonly FirewallAuditStore _auditStore;
    private readonly string _auditRoot;

    public FirewallManager(IOutputSink output, string? auditRoot = null)
    {
        _output = output;
        _auditStore = new FirewallAuditStore();
        _auditRoot = auditRoot ?? FirewallAuditStore.DefaultRoot;
    }

    public async Task<IReadOnlyList<FirewallRuleStatus>> ListRulesAsync(bool includeAllRules = false, CancellationToken ct = default)
    {
        var (exitCode, output) = await ProcessRunner.RunCapturedAsync(NetshPath, "advfirewall firewall show rule name=all verbose", ct);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Failed to list firewall rules (netsh exited with code {exitCode}).");
        }

        var results = new List<FirewallRuleStatus>();
        string? name = null;
        string? protocol = null;
        string? port = null;
        var enabled = false;

        void FinalizeBlock()
        {
            if (name is not null && (includeAllRules || name.StartsWith(RuleNamePrefix, StringComparison.Ordinal)))
            {
                results.Add(new FirewallRuleStatus
                {
                    Name = name,
                    Protocol = protocol ?? "Any",
                    Port = port ?? "Any",
                    Enabled = enabled,
                });
            }
        }

        // netsh's "verbose" output is a series of "Label:   Value" lines, one block per rule,
        // each block starting with a "Rule Name:" line -- so a new "Rule Name:" line both closes
        // the previous block and opens the next one.
        foreach (var rawLine in output.Split('\n'))
        {
            var match = FieldLineRegex().Match(rawLine.TrimEnd('\r'));
            if (!match.Success)
            {
                continue;
            }

            var key = match.Groups["key"].Value.Trim();
            var value = match.Groups["value"].Value.Trim();

            if (string.Equals(key, "Rule Name", StringComparison.Ordinal))
            {
                FinalizeBlock();
                name = value;
                protocol = null;
                port = null;
                enabled = false;
                continue;
            }

            switch (key)
            {
                case "Enabled":
                    enabled = string.Equals(value, "Yes", StringComparison.OrdinalIgnoreCase);
                    break;
                case "Protocol":
                    protocol = value;
                    break;
                case "LocalPort":
                    port = value;
                    break;
            }
        }

        FinalizeBlock();
        return results;
    }

    /// <param name="label">A short human description (e.g. "Staging site") -- the actual rule
    /// name shown in <see cref="ListRulesAsync"/> is just "[IIS] {label}"; protocol/port aren't
    /// folded into it since the grid already shows those as their own columns.</param>
    /// <param name="ports">A single port, or netsh's own comma/range syntax, e.g.
    /// "8080" or "9001,9005-9008".</param>
    /// <param name="performedBy">Recorded in the audit trail -- see <see cref="GetAuditHistoryAsync"/>.</param>
    public async Task AddInboundRuleAsync(string label, string ports, string protocol, string performedBy, CancellationToken ct = default)
    {
        ValidatePortSpec(ports);
        protocol = NormalizeProtocol(protocol);

        var ruleName = $"{RuleNamePrefix}{label}";

        var existing = await ListRulesAsync(includeAllRules: false, ct);
        if (existing.Any(r => string.Equals(r.Name, ruleName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"A firewall rule named '{ruleName}' already exists.");
        }

        var args = $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol={protocol} " +
                   $"localport={ports} description=\"Added by PublishTool\"";
        var exitCode = await ProcessRunner.RunAsync(NetshPath, args, _output, ct);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to add firewall rule '{ruleName}' (netsh exited with code {exitCode}). Try running PublishTool as Administrator.");
        }

        _output.Info($"Added inbound firewall rule '{ruleName}'.");
        await TryRecordAuditAsync(new FirewallAuditEntry
        {
            Action = "Added",
            RuleName = ruleName,
            Protocol = protocol,
            Ports = ports,
            PerformedAtUtc = DateTimeOffset.UtcNow,
            PerformedBy = performedBy,
        }, ct);
    }

    /// <summary>Updates an existing rule in place (same underlying netsh rule identity, not a
    /// delete+recreate) -- renaming it and/or changing its ports/protocol.</summary>
    public async Task EditRuleAsync(
        string currentRuleName, string newLabel, string ports, string protocol, string performedBy, CancellationToken ct = default)
    {
        ValidatePortSpec(ports);
        protocol = NormalizeProtocol(protocol);

        var newRuleName = $"{RuleNamePrefix}{newLabel}";

        var existing = await ListRulesAsync(includeAllRules: false, ct);
        var current = existing.FirstOrDefault(r => string.Equals(r.Name, currentRuleName, StringComparison.OrdinalIgnoreCase));
        if (!string.Equals(newRuleName, currentRuleName, StringComparison.OrdinalIgnoreCase) &&
            existing.Any(r => string.Equals(r.Name, newRuleName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"A firewall rule named '{newRuleName}' already exists.");
        }

        var args = $"advfirewall firewall set rule name=\"{currentRuleName}\" new name=\"{newRuleName}\" " +
                   $"localport={ports} protocol={protocol}";
        var exitCode = await ProcessRunner.RunAsync(NetshPath, args, _output, ct);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to update firewall rule '{currentRuleName}' (netsh exited with code {exitCode}). " +
                "Try running PublishTool as Administrator.");
        }

        _output.Info($"Updated firewall rule '{currentRuleName}' -> '{newRuleName}'.");
        await TryRecordAuditAsync(new FirewallAuditEntry
        {
            Action = "Edited",
            RuleName = newRuleName,
            Protocol = protocol,
            Ports = ports,
            PreviousRuleName = currentRuleName,
            PreviousProtocol = current?.Protocol,
            PreviousPorts = current?.Port,
            PerformedAtUtc = DateTimeOffset.UtcNow,
            PerformedBy = performedBy,
        }, ct);
    }

    public async Task DeleteRuleAsync(string ruleName, string performedBy, CancellationToken ct = default)
    {
        var existing = await ListRulesAsync(includeAllRules: false, ct);
        var current = existing.FirstOrDefault(r => string.Equals(r.Name, ruleName, StringComparison.OrdinalIgnoreCase));

        var exitCode = await ProcessRunner.RunAsync(NetshPath, $"advfirewall firewall delete rule name=\"{ruleName}\"", _output, ct);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to delete firewall rule '{ruleName}' (netsh exited with code {exitCode}). Try running PublishTool as Administrator.");
        }

        _output.Info($"Deleted firewall rule '{ruleName}'.");
        await TryRecordAuditAsync(new FirewallAuditEntry
        {
            Action = "Removed",
            RuleName = ruleName,
            Protocol = current?.Protocol ?? "Any",
            Ports = current?.Port ?? "Any",
            PerformedAtUtc = DateTimeOffset.UtcNow,
            PerformedBy = performedBy,
        }, ct);
    }

    /// <summary>Full Add/Edit/Remove audit trail, newest-first.</summary>
    public Task<IReadOnlyList<FirewallAuditEntry>> GetAuditHistoryAsync(CancellationToken ct = default) =>
        _auditStore.GetHistoryAsync(_auditRoot, ct);

    /// <summary>Best-effort -- a missing/unwritable audit log is a diagnostic nicety, not
    /// something that should fail an otherwise-successful firewall change.</summary>
    private async Task TryRecordAuditAsync(FirewallAuditEntry entry, CancellationToken ct)
    {
        try
        {
            await _auditStore.AppendAsync(_auditRoot, entry, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _output.Warn($"Firewall rule change succeeded, but couldn't record it in the audit trail: {ex.Message}");
        }
    }

    private static string NormalizeProtocol(string protocol)
    {
        protocol = protocol.ToUpperInvariant();
        if (protocol is not ("TCP" or "UDP"))
        {
            throw new ArgumentException("Protocol must be TCP or UDP.", nameof(protocol));
        }

        return protocol;
    }

    /// <summary>Accepts netsh's own localport syntax: a comma-separated list of single ports
    /// and/or "start-end" ranges, e.g. "8080" or "9001,9005-9008".</summary>
    public static void ValidatePortSpec(string ports)
    {
        if (string.IsNullOrWhiteSpace(ports))
        {
            throw new ArgumentException("Enter at least one port.", nameof(ports));
        }

        foreach (var segment in ports.Split(',', StringSplitOptions.TrimEntries))
        {
            var match = PortSegmentRegex().Match(segment);
            if (!match.Success)
            {
                throw new ArgumentException($"'{segment}' isn't a valid port or port range.", nameof(ports));
            }

            var start = int.Parse(match.Groups["start"].Value);
            if (start is < 1 or > 65535)
            {
                throw new ArgumentException($"'{segment}' is out of range -- ports must be between 1 and 65535.", nameof(ports));
            }

            if (match.Groups["end"].Success)
            {
                var end = int.Parse(match.Groups["end"].Value);
                if (end is < 1 or > 65535)
                {
                    throw new ArgumentException($"'{segment}' is out of range -- ports must be between 1 and 65535.", nameof(ports));
                }

                if (start > end)
                {
                    throw new ArgumentException($"'{segment}' is backwards -- the first port must be lower than the second.", nameof(ports));
                }
            }
        }
    }

    [GeneratedRegex(@"^(?<key>[^:]+):\s*(?<value>.*)$")]
    private static partial Regex FieldLineRegex();

    [GeneratedRegex(@"^(?<start>\d{1,5})(-(?<end>\d{1,5}))?$")]
    private static partial Regex PortSegmentRegex();
}
