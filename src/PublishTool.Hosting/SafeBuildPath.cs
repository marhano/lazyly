namespace PublishTool.Hosting;

/// <summary>
/// The path-traversal containment check every endpoint that takes a client-supplied path segment
/// needs -- previously duplicated (identically) across <c>/download</c>, the manual upload form,
/// and the build-files upload form. One place to get this right instead of several.
/// </summary>
internal static class SafeBuildPath
{
    /// <summary>Resolves <paramref name="relativePath"/> against <paramref name="buildsRoot"/>,
    /// returning null if the result would land outside <paramref name="buildsRoot"/> (e.g. via a
    /// "..\" segment). Does not check the file actually exists -- callers that care do that
    /// themselves afterward.</summary>
    public static string? Resolve(string buildsRoot, string relativePath)
    {
        var normalizedRoot = Path.GetFullPath(buildsRoot) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(buildsRoot, relativePath));
        return fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
    }

    /// <summary>Same containment check, for a bare project-name segment rather than an already
    /// relative file path -- used by the upload paths, which only have a project name at
    /// validation time, not yet a specific file underneath it.</summary>
    public static bool IsValidProjectName(string buildsRoot, string projectName) =>
        Resolve(buildsRoot, projectName) is not null;

    /// <summary>Relativizes an absolute on-disk path back to a <see cref="Resolve"/>-compatible
    /// token, for building API list responses without exposing the server's own absolute paths.</summary>
    public static string ToRelative(string buildsRoot, string absolutePath) =>
        Path.GetRelativePath(buildsRoot, absolutePath);
}
