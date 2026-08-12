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
}
