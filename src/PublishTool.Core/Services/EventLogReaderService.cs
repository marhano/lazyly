using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Runtime.Versioning;
using System.Security;
using PublishTool.Core.Models;

namespace PublishTool.Core.Services;

/// <summary>
/// Reads Windows Event Log entries for a project, local or remote. Unlike the classic
/// System.Diagnostics.EventLog API (which only enumerates everything and requires the caller to
/// scan/filter in memory), this uses the newer Eventing.Reader APIs so the log name, source, and
/// time window are filtered natively by the OS -- only "message contains" filtering (for apps that
/// share a generic log, e.g. via NLog into "Application" without a distinct source) still requires
/// reading candidate entries and checking the rendered message in-process, same as that older
/// approach would have.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EventLogReaderService
{
    private const int MaxRawEntriesScanned = 5000;

    public List<EventLogEntryRecord> GetRecent(EventLogQueryOptions options)
    {
        using var session = CreateSession(options);

        var xpath = BuildXPath(options);
        var query = new EventLogQuery(options.LogName, PathType.LogName, xpath)
        {
            Session = session,
            ReverseDirection = true,
        };

        var messageFilterValues = options.FilterValues.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        var messageFilterActive = string.Equals(options.FilterType, EventLogFilterTypes.MessageContains, StringComparison.OrdinalIgnoreCase)
            && messageFilterValues.Count > 0;

        var results = new List<EventLogEntryRecord>();
        using var reader = new EventLogReader(query);

        var scanned = 0;
        while (results.Count < options.MaxEntries && scanned < MaxRawEntriesScanned)
        {
            using var record = reader.ReadEvent();
            if (record is null)
            {
                break;
            }

            scanned++;

            var message = ResolveMessage(record);

            if (messageFilterActive && (message is null || !messageFilterValues.Any(v => message.Contains(v, StringComparison.OrdinalIgnoreCase))))
            {
                continue;
            }

            results.Add(new EventLogEntryRecord
            {
                TimeCreated = record.TimeCreated,
                Level = GetLevelDisplayName(record),
                Source = record.ProviderName ?? string.Empty,
                EventId = record.Id,
                Message = message ?? "(message unavailable -- provider metadata not registered on this machine)",
                MachineName = record.MachineName ?? options.MachineName ?? Environment.MachineName,
            });
        }

        return results;
    }

    private static EventLogSession CreateSession(EventLogQueryOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.MachineName))
        {
            return new EventLogSession();
        }

        if (string.IsNullOrWhiteSpace(options.Username))
        {
            // Remote, using the current Windows identity -- works when the caller's identity is
            // already trusted by the target machine (e.g. same domain).
            return new EventLogSession(options.MachineName);
        }

        using var securePassword = ToSecureString(options.Password ?? string.Empty);
        return new EventLogSession(options.MachineName, domain: null, options.Username, securePassword, SessionAuthentication.Default);
    }

    private static SecureString ToSecureString(string plainText)
    {
        var secure = new SecureString();
        foreach (var c in plainText)
        {
            secure.AppendChar(c);
        }

        secure.MakeReadOnly();
        return secure;
    }

    /// <summary>
    /// FormatDescription() needs the provider's registered message-table resources to render a
    /// template -- and returns an empty string (not an exception, not null) rather than the raw
    /// text when it can't, which is the common case for anything logged through a generic,
    /// pre-existing source like "Application" (e.g. NLog's default EventLog target) instead of a
    /// properly registered one. Falls back to the raw insertion string(s) recorded with the event,
    /// which is how the classic EventLog API's own .Message property manages to render these where
    /// FormatDescription() can't.
    /// </summary>
    private static string? ResolveMessage(EventRecord record)
    {
        try
        {
            var formatted = record.FormatDescription();
            if (!string.IsNullOrWhiteSpace(formatted))
            {
                return formatted;
            }
        }
        catch (EventLogException)
        {
        }

        try
        {
            var values = record.Properties
                .Select(p => p.Value?.ToString())
                .Where(v => !string.IsNullOrEmpty(v));
            var joined = string.Join(Environment.NewLine, values);
            return string.IsNullOrWhiteSpace(joined) ? null : joined;
        }
        catch (EventLogException)
        {
            return null;
        }
    }

    private static string GetLevelDisplayName(EventRecord record)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(record.LevelDisplayName))
            {
                return record.LevelDisplayName;
            }
        }
        catch (EventLogException)
        {
            // Same provider-metadata caveat as FormatDescription() above -- fall back to the
            // standard Windows event level numbering, which needs no metadata to interpret.
        }

        return record.Level switch
        {
            1 => "Critical",
            2 => "Error",
            3 => "Warning",
            4 => "Information",
            5 => "Verbose",
            _ => "Information",
        };
    }

    private static string BuildXPath(EventLogQueryOptions options)
    {
        var conditions = new List<string>
        {
            $"TimeCreated[timediff(@SystemTime) <= {(long)options.Lookback.TotalMilliseconds}]",
        };

        var sourceValues = options.FilterValues.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (string.Equals(options.FilterType, EventLogFilterTypes.Source, StringComparison.OrdinalIgnoreCase) && sourceValues.Count > 0)
        {
            var providerConditions = sourceValues.Select(v => $"Provider[@Name='{v.Replace("'", "&apos;")}']");
            conditions.Add($"({string.Join(" or ", providerConditions)})");
        }

        return $"*[System[{string.Join(" and ", conditions)}]]";
    }
}
