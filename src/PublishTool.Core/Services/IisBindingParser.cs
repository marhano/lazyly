using System.Text.RegularExpressions;

namespace PublishTool.Core.Services;

/// <summary>Parses the raw <c>protocol/ip:port:hostname</c> binding strings appcmd reports (see
/// <see cref="Models.IisSiteStatus.Bindings"/>) back into port numbers -- e.g. for relaying every
/// port an IIS site uses without needing a structured binding model round-tripped from the server.</summary>
public static partial class IisBindingParser
{
    public static IReadOnlyList<int> ExtractPorts(string bindings)
    {
        var ports = new List<int>();
        foreach (var segment in bindings.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = BindingRegex().Match(segment);
            if (match.Success && int.TryParse(match.Groups["port"].Value, out var port))
            {
                ports.Add(port);
            }
        }

        return ports;
    }

    [GeneratedRegex(@"^\w+/[^:]+:(?<port>\d+):.*$")]
    private static partial Regex BindingRegex();
}
