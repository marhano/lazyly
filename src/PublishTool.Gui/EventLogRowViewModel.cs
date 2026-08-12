using System.Text.RegularExpressions;
using System.Windows.Media;
using PublishTool.Core.Models;

namespace PublishTool.Gui;

/// <summary>Wraps an <see cref="EventLogEntryRecord"/> for display in the Event Logs grid --
/// adds the formatted/truncated display strings and level-based pill colors the raw record
/// doesn't carry.</summary>
public sealed class EventLogRowViewModel
{
    // Same three message shapes OmniBusiness's own EventLogService looks for (its
    // OPIBizAPIService.cs embeds the calling method's nameof(...) into these fixed sentences).
    // Anything else just doesn't get a MethodName -- it still shows up in the grid, it's only
    // excluded from the Method filter, same as the reference behaves.
    private static readonly Regex[] MethodNamePatterns =
    {
        new(@"Request payload for (\w+):", RegexOptions.Compiled),
        new(@"API Response for (\w+):", RegexOptions.Compiled),
        new(@"Exception occured during OPIBiz API (\w+) call:", RegexOptions.Compiled),
    };

    public EventLogRowViewModel(EventLogEntryRecord record)
    {
        Record = record;
        MethodName = ExtractMethodName(record.Message);
    }

    public EventLogEntryRecord Record { get; }

    public string TimeDisplay => Record.TimeCreated?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "";

    public string Level => Record.Level;

    public string Source => Record.Source;

    public int EventId => Record.EventId;

    public string FullMessage => Record.Message;

    public string MessageSummary => Truncate(Record.Message, 160);

    /// <summary>Extracted from the message body via <see cref="MethodNamePatterns"/>, or null if
    /// it doesn't match a known shape.</summary>
    public string? MethodName { get; }

    public Brush LevelBackground => LevelBrushes.GetBackground(Record.Level);

    public Brush LevelForeground => LevelBrushes.GetForeground(Record.Level);

    /// <summary>Everything a free-text search box should match against.</summary>
    public bool MatchesSearch(string search) =>
        Record.Message.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        Record.Source.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        Record.Level.Contains(search, StringComparison.OrdinalIgnoreCase);

    private static string? ExtractMethodName(string message)
    {
        foreach (var pattern in MethodNamePatterns)
        {
            var match = pattern.Match(message);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }

    private static string Truncate(string text, int maxLength)
    {
        var singleLine = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        return singleLine.Length > maxLength ? singleLine[..maxLength] + "…" : singleLine;
    }
}

internal static class LevelBrushes
{
    private static readonly Brush ErrorBackground = Freeze(0x22, 0xB2, 0x3A, 0x3A);
    private static readonly Brush ErrorForeground = Freeze(0xFF, 0xB2, 0x3A, 0x3A);
    private static readonly Brush WarningBackground = Freeze(0x22, 0xB8, 0x72, 0x1A);
    private static readonly Brush WarningForeground = Freeze(0xFF, 0xB8, 0x72, 0x1A);
    private static readonly Brush NeutralBackground = Freeze(0x22, 0x80, 0x80, 0x80);
    private static readonly Brush NeutralForeground = Freeze(0xFF, 0x70, 0x70, 0x70);

    public static Brush GetBackground(string level) => level switch
    {
        "Critical" or "Error" => ErrorBackground,
        "Warning" => WarningBackground,
        _ => NeutralBackground,
    };

    public static Brush GetForeground(string level) => level switch
    {
        "Critical" or "Error" => ErrorForeground,
        "Warning" => WarningForeground,
        _ => NeutralForeground,
    };

    private static Brush Freeze(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
