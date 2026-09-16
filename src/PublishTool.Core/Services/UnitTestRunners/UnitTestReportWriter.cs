using System.Linq;
using ClosedXML.Excel;
using PublishTool.Core.Models;

namespace PublishTool.Core.Services.UnitTestRunners;

/// <summary>Writes a list of <see cref="UnitTestCaseResult"/> to an .xlsx report -- one sheet, a
/// summary line, then one row per test case (across every suite that was run, distinguished by the
/// "Suite" column). This is the only place ClosedXML is used in this codebase; kept isolated here
/// so the dependency stays easy to swap out later if needed.</summary>
public static class UnitTestReportWriter
{
    public static void Write(IReadOnlyList<UnitTestCaseResult> cases, string outputPath)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Test Results");

        var passed = cases.Count(c => string.Equals(c.Outcome, "Passed", StringComparison.OrdinalIgnoreCase));
        var failed = cases.Count(c => string.Equals(c.Outcome, "Failed", StringComparison.OrdinalIgnoreCase));
        var skipped = cases.Count - passed - failed;
        sheet.Cell(1, 1).Value = $"{cases.Count} total, {passed} passed, {failed} failed, {skipped} skipped";
        sheet.Range(1, 1, 1, 6).Merge();
        sheet.Cell(1, 1).Style.Font.Bold = true;

        var headerRow = sheet.Row(3);
        headerRow.Cell(1).Value = "Suite";
        headerRow.Cell(2).Value = "Test Name";
        headerRow.Cell(3).Value = "Class";
        headerRow.Cell(4).Value = "Outcome";
        headerRow.Cell(5).Value = "Duration (s)";
        headerRow.Cell(6).Value = "Error Message";
        headerRow.Style.Font.Bold = true;

        var row = 4;
        foreach (var testCase in cases)
        {
            sheet.Cell(row, 1).Value = testCase.SuiteName;
            sheet.Cell(row, 2).Value = testCase.Name;
            sheet.Cell(row, 3).Value = testCase.ClassName ?? string.Empty;
            sheet.Cell(row, 4).Value = testCase.Outcome;
            sheet.Cell(row, 5).Value = Math.Round(testCase.Duration.TotalSeconds, 3);
            sheet.Cell(row, 6).Value = testCase.ErrorMessage ?? string.Empty;
            row++;
        }

        sheet.Columns(1, 6).AdjustToContents();
        workbook.SaveAs(outputPath);
    }
}
