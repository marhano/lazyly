namespace PublishTool.Core.Models;

/// <summary>A single Windows Event Log entry, normalized across local and remote reads.</summary>
public sealed class EventLogEntryRecord
{
    public DateTimeOffset? TimeCreated { get; set; }

    /// <summary>Critical, Error, Warning, Information, or Verbose.</summary>
    public required string Level { get; set; }

    /// <summary>The entry's Source/Provider name.</summary>
    public required string Source { get; set; }

    public int EventId { get; set; }

    public required string Message { get; set; }

    public required string MachineName { get; set; }
}
