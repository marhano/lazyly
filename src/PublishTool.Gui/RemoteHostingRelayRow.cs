namespace PublishTool.Gui;

/// <summary>Display row for the Settings tab's Dev Server Relays grid -- wraps a
/// <c>RemoteHostingRelay</c> with a computed <see cref="IsActive"/> flag (whether its URL matches
/// whatever's currently saved as the active Remote Build Hosting URL) so the grid can show which
/// one, if any, is currently in use.</summary>
public sealed class RemoteHostingRelayRow
{
    public required string Name { get; set; }

    public required string Url { get; set; }

    public bool IsActive { get; set; }
}
