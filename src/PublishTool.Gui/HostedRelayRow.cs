namespace PublishTool.Gui;

public sealed class HostedRelayRow
{
    public required int ListenPort { get; set; }
    public required string ConnectAddress { get; set; }
    public required int ConnectPort { get; set; }

    public string ForwardsTo => $"{ConnectAddress}:{ConnectPort}";

    /// <summary>Dev server IIS site name(s) currently bound to <see cref="ConnectPort"/>, comma-joined,
    /// or empty if this relay doesn't line up with any (e.g. it targets the Hosting API's own port).
    /// Best-effort, computed by cross-referencing the dev server's live site list -- purely a display
    /// hint, never persisted.</summary>
    public string MatchedSites { get; set; } = string.Empty;
}
