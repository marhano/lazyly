namespace PublishTool.Core.Services.AppConfig;

/// <summary>Shared recursive file search for <see cref="IAppConfigProvider.FindCandidateConfigPaths"/>
/// implementations -- walks a project's source tree looking for files by name, skipping the heavy
/// generated/dependency folders no config file would ever live under anyway.</summary>
internal static class ConfigFileSearch
{
    private static readonly string[] ExcludedDirectoryNames =
    [
        "bin", "obj", "node_modules", ".git", ".angular", "dist", "www", "platforms", "android", "ios",
    ];

    public static IReadOnlyList<string> FindFiles(string root, Func<string, bool> fileNameMatches)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return [];
        }

        var results = new List<string>();
        Walk(root, fileNameMatches, results);
        return results;
    }

    private static void Walk(string dir, Func<string, bool> fileNameMatches, List<string> results)
    {
        IEnumerable<string> files;
        IEnumerable<string> subdirectories;
        try
        {
            files = Directory.EnumerateFiles(dir);
            subdirectories = Directory.EnumerateDirectories(dir);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        foreach (var file in files)
        {
            if (fileNameMatches(Path.GetFileName(file)))
            {
                results.Add(file);
            }
        }

        foreach (var subdirectory in subdirectories)
        {
            var name = Path.GetFileName(subdirectory);
            if (!ExcludedDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                Walk(subdirectory, fileNameMatches, results);
            }
        }
    }
}
