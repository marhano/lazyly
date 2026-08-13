using System.Security.Cryptography;
using System.Text;
using PublishTool.Core.Services;

namespace PublishTool.Hosting;

/// <summary>
/// Guards the <c>/api/*</c> endpoints, which -- unlike the rest of this site -- are meant for a
/// detached PublishTool.Gui client rather than a browser, so they need their own check instead of
/// relying on whatever network restrictions keep the human pages "safe enough" today.
/// </summary>
internal static class ApiKeyAuth
{
    /// <summary>True only if the server has an API key configured AND the request's
    /// <see cref="RemoteHostingClient.ApiKeyHeaderName"/> header matches it exactly. A missing
    /// server-side key always rejects (fail-closed) -- a forgotten config must not mean "everyone's
    /// allowed in." Comparison is constant-time to avoid leaking the key via response-timing.</summary>
    public static bool Validate(HttpRequest request, IConfiguration configuration)
    {
        var configuredKey = configuration["ApiKey"];
        if (string.IsNullOrEmpty(configuredKey))
        {
            return false;
        }

        if (!request.Headers.TryGetValue(RemoteHostingClient.ApiKeyHeaderName, out var provided) || provided.Count == 0)
        {
            return false;
        }

        var configuredBytes = Encoding.UTF8.GetBytes(configuredKey);
        var providedBytes = Encoding.UTF8.GetBytes(provided[0] ?? string.Empty);

        // Lengths must match for FixedTimeEquals to even compare -- doing the length check first
        // is itself technically a (much smaller) timing signal, but comparing two different-length
        // byte arrays byte-by-byte isn't meaningfully possible any other way with this API.
        return configuredBytes.Length == providedBytes.Length && CryptographicOperations.FixedTimeEquals(configuredBytes, providedBytes);
    }
}
