namespace PublishTool.Core.Models;

/// <summary>
/// One saved alternate route to reach the same Remote Build Hosting dev server -- e.g. a
/// colleague's already-office-network-connected machine relaying the connection (via a Windows
/// <c>netsh interface portproxy</c> forward) for someone whose own network (VPN, etc.) doesn't
/// route to the dev server's subnet directly. Just a friendly label plus the URL to use instead of
/// <see cref="AppSettings.RemoteHostingUrl"/> -- the API key is unaffected by which relay is active,
/// since it's still the same dev server underneath, just reached a different way.
/// </summary>
public sealed class RemoteHostingRelay
{
    public required string Name { get; set; }

    public required string Url { get; set; }
}
