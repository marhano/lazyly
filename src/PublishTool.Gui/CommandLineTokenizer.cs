using System.Text;

namespace PublishTool.Gui;

/// <summary>
/// Splits a single line typed into the Command tab into argv-style tokens,
/// so it can be handed to the same parser the CLI uses.
/// </summary>
internal static class CommandLineTokenizer
{
    public static string[] Tokenize(string input)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var c in input)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens.ToArray();
    }
}
