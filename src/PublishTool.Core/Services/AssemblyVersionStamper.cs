using System.Text;
using System.Text.RegularExpressions;

namespace PublishTool.Core.Services;

public static partial class AssemblyVersionStamper
{
    /// <summary>
    /// Stamps a version into AssemblyInfo.cs. Classic AssemblyVersion/AssemblyFileVersion attributes
    /// only accept a strict major[.minor[.build[.revision]]] numeric format, so semver-style versions
    /// (e.g. "1.2.3-beta") are truncated to their numeric prefix for those two attributes. The full,
    /// unmodified version string is stamped into AssemblyInformationalVersion instead (added if absent),
    /// so the descriptive version is still recoverable from the built assembly.
    /// </summary>
    public static void Stamp(string assemblyInfoPath, string version)
    {
        if (!File.Exists(assemblyInfoPath))
        {
            throw new FileNotFoundException("AssemblyInfo.cs not found.", assemblyInfoPath);
        }

        var numericVersion = ExtractNumericVersion(version);

        var hasBom = FileStartsWithUtf8Bom(assemblyInfoPath);
        var content = File.ReadAllText(assemblyInfoPath);
        content = AssemblyVersionRegex().Replace(content, $"[assembly: AssemblyVersion(\"{numericVersion}\")]");
        content = AssemblyFileVersionRegex().Replace(content, $"[assembly: AssemblyFileVersion(\"{numericVersion}\")]");
        content = StampInformationalVersion(content, version);

        // File.WriteAllText defaults to no-BOM UTF-8; preserve the original file's BOM
        // (Visual Studio writes AssemblyInfo.cs with one) so the diff stays version-only.
        File.WriteAllText(assemblyInfoPath, content, new UTF8Encoding(hasBom));
    }

    private static string ExtractNumericVersion(string version)
    {
        var match = NumericPrefixRegex().Match(version);
        if (!match.Success)
        {
            throw new ArgumentException(
                $"Version '{version}' has no numeric major[.minor[.build[.revision]]] prefix; " +
                "AssemblyVersion requires one (e.g. '1.2.3' or '1.2.3-beta').",
                nameof(version));
        }

        return match.Value;
    }

    private static string StampInformationalVersion(string content, string fullVersion)
    {
        if (AssemblyInformationalVersionRegex().IsMatch(content))
        {
            return AssemblyInformationalVersionRegex().Replace(
                content, $"[assembly: AssemblyInformationalVersion(\"{fullVersion}\")]");
        }

        return AssemblyFileVersionRegex().Replace(
            content, m => m.Value + Environment.NewLine + $"[assembly: AssemblyInformationalVersion(\"{fullVersion}\")]");
    }

    [GeneratedRegex(@"\[assembly:\s*AssemblyVersion\(""[^""]*""\)\]")]
    private static partial Regex AssemblyVersionRegex();

    [GeneratedRegex(@"\[assembly:\s*AssemblyFileVersion\(""[^""]*""\)\]")]
    private static partial Regex AssemblyFileVersionRegex();

    [GeneratedRegex(@"\[assembly:\s*AssemblyInformationalVersion\(""[^""]*""\)\]")]
    private static partial Regex AssemblyInformationalVersionRegex();

    [GeneratedRegex(@"^\d+(\.\d+){0,3}")]
    private static partial Regex NumericPrefixRegex();

    private static bool FileStartsWithUtf8Bom(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> preamble = stackalloc byte[3];
        var bytesRead = stream.Read(preamble);
        return bytesRead == 3 && preamble[0] == 0xEF && preamble[1] == 0xBB && preamble[2] == 0xBF;
    }
}
