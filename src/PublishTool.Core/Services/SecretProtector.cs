using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace PublishTool.Core.Services;

/// <summary>
/// Encrypts/decrypts short secrets (e.g. an optionally-saved remote Event Log password, or the
/// Remote Build Hosting API key) using Windows DPAPI, scoped to the current Windows user. The
/// encrypted blob only decrypts for the same Windows account on the same machine -- never store
/// it expecting it to travel with a copied projects.json/settings.json, and always fail
/// gracefully (re-prompt) rather than crash if it doesn't.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SecretProtector
{
    private const string DefaultPurpose = "PublishTool.EventLog";

    /// <summary>Purpose string for the Remote Build Hosting API key (see <see cref="AppSettings.RemoteHostingProtectedApiKey"/>).</summary>
    public const string RemoteHostingPurpose = "PublishTool.RemoteHosting";

    /// <param name="purpose">Distinguishes secrets from different features so a blob saved for
    /// one (e.g. the remote hosting API key) can never be mistaken for/misapplied as another (e.g.
    /// an Event Log password) -- defaults to the original Event Log purpose for callers that
    /// existed before this parameter did.</param>
    public static string Protect(string plainText, string purpose = DefaultPurpose)
    {
        var bytes = Encoding.UTF8.GetBytes(plainText);
        var protectedBytes = ProtectedData.Protect(bytes, Entropy(purpose), DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    /// <summary>Returns null (rather than throwing) if the blob can't be decrypted -- e.g. it was
    /// saved by a different Windows user/machine, saved under a different purpose, or
    /// projects.json/settings.json was hand-edited/corrupted.</summary>
    public static string? TryUnprotect(string protectedText, string purpose = DefaultPurpose)
    {
        try
        {
            var protectedBytes = Convert.FromBase64String(protectedText);
            var bytes = ProtectedData.Unprotect(protectedBytes, Entropy(purpose), DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    private static byte[] Entropy(string purpose) => Encoding.UTF8.GetBytes(purpose);
}
