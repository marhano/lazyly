using System.Text.Json;

namespace PublishTool.Gui;

/// <summary>
/// Pretty-prints the JSON payload embedded in a log message, same idea as the reference logs
/// page's beautifyMessage/tryBeautifyJson: most of these entries look like "Some prefix text:
/// {...json...}" (e.g. "API Response for InquireCollectionBalanceAsync: {...}"), so this finds
/// where a JSON object/array starts, leaves the prefix untouched, and re-indents just the JSON
/// part. Falls back to the original text completely unchanged if it doesn't find valid JSON --
/// this only ever changes formatting, never the actual content.
/// </summary>
public static class JsonMessageBeautifier
{
    private static readonly JsonSerializerOptions PrettyPrintOptions = new() { WriteIndented = true };

    public static string Beautify(string message)
    {
        var jsonStart = FindJsonStart(message);
        if (jsonStart < 0)
        {
            return message;
        }

        var prefix = message[..jsonStart];
        var candidate = message[jsonStart..].TrimEnd();

        if (!TryPrettyPrint(candidate, out var pretty))
        {
            return message;
        }

        return prefix + pretty;
    }

    private static int FindJsonStart(string message)
    {
        var braceIndex = message.IndexOf('{');
        var bracketIndex = message.IndexOf('[');

        if (braceIndex < 0)
        {
            return bracketIndex;
        }

        if (bracketIndex < 0)
        {
            return braceIndex;
        }

        return Math.Min(braceIndex, bracketIndex);
    }

    private static bool TryPrettyPrint(string candidate, out string pretty)
    {
        try
        {
            using var document = JsonDocument.Parse(candidate);
            pretty = JsonSerializer.Serialize(document.RootElement, PrettyPrintOptions);
            return true;
        }
        catch (JsonException)
        {
            // Not valid JSON on its own -- e.g. a stray '{' in unrelated text, or trailing
            // content after the JSON that isn't part of it. Leave the message as-is.
            pretty = string.Empty;
            return false;
        }
    }
}
