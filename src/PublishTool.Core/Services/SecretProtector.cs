using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace PublishTool.Core.Services;

/// <summary>
/// Encrypts/decrypts short secrets (e.g. an optionally-saved remote Event Log password) using
/// Windows DPAPI, scoped to the current Windows user. The encrypted blob only decrypts for the
/// same Windows account on the same machine -- never store it expecting it to travel with a
/// copied projects.json, and always fail gracefully (re-prompt) rather than crash if it doesn't.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SecretProtector
{
    private static readonly byte[] Entropy = "PublishTool.EventLog"u8.ToArray();

    public static string Protect(string plainText)
    {
        var bytes = Encoding.UTF8.GetBytes(plainText);
        var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    /// <summary>Returns null (rather than throwing) if the blob can't be decrypted -- e.g. it was
    /// saved by a different Windows user/machine, or projects.json was hand-edited/corrupted.</summary>
    public static string? TryUnprotect(string protectedText)
    {
        try
        {
            var protectedBytes = Convert.FromBase64String(protectedText);
            var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
