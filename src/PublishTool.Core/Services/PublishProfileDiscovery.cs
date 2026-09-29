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
}
