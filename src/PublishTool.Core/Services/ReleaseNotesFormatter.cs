using System.Globalization;
using System.Text;
using PublishTool.Core.Models;

namespace PublishTool.Core.Services;

/// <summary>
/// Renders a <see cref="ReleaseNotesEntry"/> into the exact plain-text template the business
/// expects for release notes documents. The separator widths and section layout are load-bearing
/// -- this must byte-for-byte match the approved template, not just look similar.
/// </summary>
public static class ReleaseNotesFormatter
{
    private static readonly string EqualsLine = new('=', 97);
    private static readonly string DashLine = new('-', 181);

    public static string Format(ReleaseNotesEntry entry)
    {
        var sb = new StringBuilder();

        void Line(string text = "") => sb.Append(text).Append("\r\n");

        void Section(string header, IReadOnlyList<string> items)
        {
            Line(header);
            if (items.Count == 0)
            {
                Line();
            }
            else
            {
                foreach (var item in items)
                {
                    Line($"\t- {item}");
                }
            }

            Line(DashLine);
            Line();
        }

        Line(EqualsLine);
        Line($"Reference: {entry.Reference}");
        Line($"Version: {entry.Version}");
        Line($"Date: {entry.Date:dd MMMM yyyy}");
        Line(EqualsLine);
        Line();

        Line("1. Introduction ");
        Line("This document outlines the latest features, updates, fixes, and backlog items. ");
        Line(DashLine);
        Line();

        Section("2. Features and Enhancements ", entry.Features);
        Section("3. Fixes ", entry.Fixes);
        Section("4. Other Updates ", entry.OtherUpdates);

        Line("5. Backlog Items");
        if (entry.BacklogItems.Count == 0)
        {
            Line("\tNone");
        }
        else
        {
            foreach (var item in entry.BacklogItems)
            {
                Line($"\t- {item}");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Reverse of <see cref="Format"/> -- reconstructs a <see cref="ReleaseNotesEntry"/> from
    /// previously-formatted text, so an existing build's release notes can be reloaded into the
    /// editors when the user picks that version again (to review/edit before overwriting) instead
    /// of starting from a blank form. Returns null if the text doesn't look like our template
    /// (e.g. no "Reference:" line) rather than throwing -- callers treat that as "no notes to load".
    /// </summary>
    public static ReleaseNotesEntry? Parse(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');

        string? reference = null;
        string? version = null;
        DateOnly? date = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("Reference: ", StringComparison.Ordinal))
            {
                reference = line["Reference: ".Length..].Trim();
            }
            else if (line.StartsWith("Version: ", StringComparison.Ordinal))
            {
                version = line["Version: ".Length..].Trim();
            }
            else if (line.StartsWith("Date: ", StringComparison.Ordinal) &&
                     DateOnly.TryParseExact(line["Date: ".Length..].Trim(), "dd MMMM yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
            {
                date = parsedDate;
            }
        }

        if (reference is null)
        {
            return null;
        }

        return new ReleaseNotesEntry
        {
            Reference = reference,
            Version = version ?? string.Empty,
            Date = date ?? DateOnly.FromDateTime(DateTime.Now),
            Features = ExtractSection(lines, "2. Features and Enhancements "),
            Fixes = ExtractSection(lines, "3. Fixes "),
            OtherUpdates = ExtractSection(lines, "4. Other Updates "),
            BacklogItems = ExtractBacklogItems(lines),
        };
    }

    private static List<string> ExtractSection(string[] lines, string header)
    {
        var items = new List<string>();
        var headerIndex = Array.IndexOf(lines, header);
        if (headerIndex < 0)
        {
            return items;
        }

        for (var i = headerIndex + 1; i < lines.Length; i++)
        {
            if (lines[i] == DashLine)
            {
                break;
            }

            if (lines[i].Length > 0)
            {
                items.Add(StripItemPrefix(lines[i]));
            }
        }

        return items;
    }

    private static List<string> ExtractBacklogItems(string[] lines)
    {
        var items = new List<string>();
        var headerIndex = Array.IndexOf(lines, "5. Backlog Items");
        if (headerIndex < 0)
        {
            return items;
        }

        for (var i = headerIndex + 1; i < lines.Length; i++)
        {
            if (lines[i].Length == 0)
            {
                continue;
            }

            var item = StripItemPrefix(lines[i]);
            if (!string.Equals(item, "None", StringComparison.OrdinalIgnoreCase))
            {
                items.Add(item);
            }
        }

        return items;
    }

    private static string StripItemPrefix(string line)
    {
        var trimmed = line.TrimStart('\t');
        return trimmed.StartsWith("- ", StringComparison.Ordinal) ? trimmed[2..] : trimmed;
    }
}
