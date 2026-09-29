using System.Xml.Linq;

namespace PublishTool.Core.Services;

/// <summary>Scans a .NET project's Properties\PublishProfiles folder for the names of its
/// available .pubxml publish profiles, so the Publish tab's profile select always reflects
/// whatever profiles actually exist on disk rather than a free-text field the user could
/// mistype.</summary>
public static class PublishProfileDiscovery
{
    public static IReadOnlyList<string> FindProfileNames(string? csprojPath)
    {
        var projectDir = string.IsNullOrWhiteSpace(csprojPath) ? null : Path.GetDirectoryName(csprojPath);
        if (string.IsNullOrWhiteSpace(projectDir))
        {
            return Array.Empty<string>();
        }

        var profilesDir = Path.Combine(projectDir, "Properties", "PublishProfiles");
        if (!Directory.Exists(profilesDir))
        {
            return Array.Empty<string>();
        }

        return Directory.GetFiles(profilesDir, "*.pubxml")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Reads a profile's own &lt;LastUsedBuildConfiguration&gt; (e.g. "Release-AsensoPay"
    /// for a multi-brand project with per-brand MSBuild configurations/DefineConstants/config
    /// transforms) -- this is what Visual Studio itself switches the active configuration to before
    /// invoking a publish with this profile selected, and MSBuild's web publish pipeline does NOT
    /// infer it automatically from just /p:PublishProfile, so a caller building via the command line
    /// (as this app does) must read and pass it explicitly or every profile silently builds as
    /// whatever the default configuration is instead of its own. Null if the profile has no such
    /// element, can't be found, or can't be parsed -- callers should fall back to "Release".</summary>
    public static string? ReadBuildConfiguration(string? csprojPath, string? pubxmlName)
    {
        var projectDir = string.IsNullOrWhiteSpace(csprojPath) ? null : Path.GetDirectoryName(csprojPath);
        if (string.IsNullOrWhiteSpace(projectDir) || string.IsNullOrWhiteSpace(pubxmlName))
        {
            return null;
        }

        var pubxmlPath = Path.Combine(projectDir, "Properties", "PublishProfiles", $"{pubxmlName}.pubxml");
        if (!File.Exists(pubxmlPath))
        {
            return null;
        }

        try
        {
            var root = XDocument.Load(pubxmlPath).Root;
            var ns = root?.Name.Namespace ?? XNamespace.None;
            var value = root?.Elements(ns + "PropertyGroup")
                .Elements(ns + "LastUsedBuildConfiguration")
                .FirstOrDefault()?.Value;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
