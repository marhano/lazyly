using System.Text.RegularExpressions;

namespace PublishTool.Core.Services;

/// <summary>
/// Reads/writes key/value pairs inside a TypeScript file's exported object literal -- e.g.
/// <c>export const environment = { production: false, apiUrl: '...' };</c> or
/// <c>const config: CapacitorConfig = { appId: '...', appName: '...' };</c>. Handles an optional
/// type annotation (or none, for a project with no interface to annotate with) between the
/// variable name and <c>=</c>, and flattens nested objects (e.g. <c>version: { app: '1.0.1' }</c>)
/// to dotted keys (<c>version.app</c>) rather than skipping or misreading them. Only the declared
/// object literal's own lines are touched -- everything before/after it (imports, other exports)
/// is left alone.
///
/// Shared by <see cref="AppConfig.EnvironmentTsProvider"/> (the <c>environment</c> variable) and
/// the Capacitor wrapper strategy's app-metadata reader (the <c>config</c> variable) -- both need
/// the exact same "find a named object literal, walk its properties tracking brace depth" parsing,
/// just for a different variable name.
/// </summary>
public static partial class TsObjectLiteral
{
    public readonly record struct Property(string DottedKey, int LineIndex, Group ValueGroup);

    public static Dictionary<string, string> Read(string configPath, string variableName)
    {
        var lines = File.ReadAllLines(configPath);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in EnumerateProperties(lines, variableName))
        {
            result[property.DottedKey] = Unquote(property.ValueGroup.Value);
        }

        return result;
    }

    public static void Write(string configPath, string variableName, IReadOnlyDictionary<string, string> settings)
    {
        var lines = File.ReadAllLines(configPath);

        foreach (var property in EnumerateProperties(lines, variableName))
        {
            if (!settings.TryGetValue(property.DottedKey, out var newValue))
            {
                continue;
            }

            var line = lines[property.LineIndex];
            var valueGroup = property.ValueGroup;
            var replacement = Requote(valueGroup.Value, newValue);
            lines[property.LineIndex] = line[..valueGroup.Index] + replacement + line[(valueGroup.Index + valueGroup.Length)..];
        }

        File.WriteAllLines(configPath, lines);
    }

    /// <summary>Walks the declared object literal's lines tracking brace depth, so a nested object
    /// contributes dotted-path entries instead of either being missed or misread as flat top-level
    /// keys. Deliberately narrow -- only descends into <c>key: {</c>-shaped nested objects, and
    /// stops at the declaration's own closing brace -- rather than regex-matching "key: value"
    /// anywhere in the file, so this can't misfire on unrelated code elsewhere.</summary>
    public static IEnumerable<Property> EnumerateProperties(string[] lines, string variableName)
    {
        var declarationRegex = BuildDeclarationRegex(variableName);
        var start = Array.FindIndex(lines, declarationRegex.IsMatch);
        if (start < 0)
        {
            yield break;
        }

        var pathStack = new Stack<string>();

        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith('}'))
            {
                if (pathStack.Count == 0)
                {
                    // Back to depth 0 -- this is the declaration's own closing brace, we're done.
                    yield break;
                }

                pathStack.Pop();
                continue;
            }

            var nestedOpen = NestedObjectOpenRegex().Match(line);
            if (nestedOpen.Success)
            {
                pathStack.Push(nestedOpen.Groups["key"].Value);
                continue;
            }

            // Quoted values are matched by their actual closing quote, not by "where a comma or
            // // shows up" -- a string value containing "://" (any URL) or a comma would otherwise
            // get truncated by a naive non-greedy-up-to-comment pattern. Only bare (unquoted)
            // values -- booleans/numbers/identifiers, which never contain either -- use the
            // simpler fallback.
            var property = QuotedPropertyLineRegex().Match(line);
            if (!property.Success)
            {
                property = BarePropertyLineRegex().Match(line);
            }

            if (!property.Success)
            {
                continue;
            }

            var dottedKey = pathStack.Count == 0
                ? property.Groups["key"].Value
                : string.Join('.', pathStack.Reverse().Append(property.Groups["key"].Value));
            yield return new Property(dottedKey, i, property.Groups["value"]);
        }
    }

    public static string Unquote(string rawValue)
    {
        var trimmed = rawValue.Trim();
        return trimmed.Length >= 2 && trimmed[0] is '\'' or '"' or '`' && trimmed[^1] == trimmed[0]
            ? trimmed[1..^1]
            : trimmed;
    }

    /// <summary>Re-quotes <paramref name="newValue"/> the same way <paramref name="rawOldValue"/>
    /// was quoted (escaping any instances of that same quote character in the new value) -- or,
    /// for an originally-unquoted value (boolean/number/bare identifier), writes it back exactly
    /// as given, trusting it's still valid TypeScript.</summary>
    public static string Requote(string rawOldValue, string newValue)
    {
        var trimmed = rawOldValue.Trim();
        if (trimmed.Length >= 2 && trimmed[0] is '\'' or '"' or '`' && trimmed[^1] == trimmed[0])
        {
            var quote = trimmed[0];
            return quote + newValue.Replace(quote.ToString(), "\\" + quote) + quote;
        }

        return newValue;
    }

    // Not source-generated (GeneratedRegex requires a compile-time constant pattern) since
    // variableName is only known at runtime -- an infrequent, non-hot-path parse, so the small
    // extra construction cost doesn't matter. Accepts an optional type annotation (or none, for a
    // project with no interface to annotate with) between the name and "=".
    private static Regex BuildDeclarationRegex(string variableName) =>
        new($@"^\s*(export\s+)?(const|let|var)\s+{Regex.Escape(variableName)}\b[^=\r\n]*=\s*\{{\s*$");

    // A line that opens a nested object -- "key: {" with nothing after the brace but whitespace
    // and/or a comment. Checked before the property patterns below, which would otherwise happily
    // match "{" itself as a (wrong) scalar value.
    [GeneratedRegex(@"^\s*(?<key>[A-Za-z_$][\w$]*)\s*:\s*\{\s*(//.*)?$")]
    private static partial Regex NestedObjectOpenRegex();

    // "value" spans the whole quoted token, quotes included (Unquote/Requote expect that) -- the
    // inner (?:\\.|(?!\k<q>).)* walks character-by-character, stopping only at an actual unescaped
    // matching quote, so a "//" or "," inside the string (a URL, most commonly) can't be mistaken
    // for the trailing comma/comment that follows the closing quote.
    [GeneratedRegex(@"^\s*(?<key>[A-Za-z_$][\w$]*)\s*:\s*(?<value>(?<q>['""`])(?:\\.|(?!\k<q>).)*\k<q>)\s*,?\s*(//.*)?$")]
    private static partial Regex QuotedPropertyLineRegex();

    // Fallback for unquoted values (booleans/numbers/bare identifiers) -- these never legitimately
    // contain "," or "//", so the simpler non-greedy-to-end-of-line approach is safe here.
    [GeneratedRegex(@"^\s*(?<key>[A-Za-z_$][\w$]*)\s*:\s*(?<value>[^,\r\n]+?)\s*,?\s*(//.*)?$")]
    private static partial Regex BarePropertyLineRegex();
}
