namespace PublishTool.Core.Models;

public static class EventLogFilterTypes
{
    public const string Source = "Source";
    public const string MessageContains = "MessageContains";
}

public sealed class EventLogQueryOptions
{
    public required string LogName { get; set; }

    /// <summary>Null/empty means the local machine.</summary>
    public string? MachineName { get; set; }

    /// <summary>Null uses the current Windows identity -- only meaningful (and required) for a
    /// remote <see cref="MachineName"/> that doesn't trust the current identity.</summary>
    public string? Username { get; set; }

    /// <summary>Plaintext, in-memory only -- never written to disk by this type. See
    /// <see cref="Services.SecretProtector"/> for the caller-side persistence story.</summary>
    public string? Password { get; set; }

    /// <summary>One of <see cref="EventLogFilterTypes"/>.</summary>
    public required string FilterType { get; set; }

    /// <summary>The Source name(s) or message substring(s) to filter by, depending on
    /// <see cref="FilterType"/> -- matches if a record matches ANY value. Empty means no filtering
    /// beyond the log name and lookback.</summary>
    public IReadOnlyList<string> FilterValues { get; set; } = Array.Empty<string>();

    public int MaxEntries { get; set; } = 500;

    public TimeSpan Lookback { get; set; } = TimeSpan.FromDays(7);
}
