using System.Linq;
using PublishTool.Core.Models;

namespace PublishTool.Core.Services.UnitTestRunners;

/// <summary>One suite's aggregate outcome from an orchestrated run -- the in-memory counterpart to
/// <see cref="TestSuiteRunStatus"/> (which is what actually gets persisted). <paramref name="FailedCount"/>
/// counts only cases whose outcome is literally "Failed" -- a skipped/not-executed case (e.g. NUnit's
/// "NotExecuted"/"Inconclusive") is neither passed nor failed, and must not be counted as a failure
/// just because it isn't "Passed" (that conflation previously inflated the reported failure count by
/// however many tests were skipped, e.g. reporting "269 failed" when only 234 genuinely failed and 35
/// were skipped).</summary>
public sealed record SuiteRunSummary(string SuiteName, bool Passed, int TotalCount, int PassedCount, int FailedCount, int SkippedCount);

/// <summary><paramref name="ReportPath"/> is one combined .xlsx across every suite that produced
/// test cases, or null if none did. <paramref name="Passed"/> is true only if every suite in
/// <paramref name="Suites"/> passed -- a suite that couldn't even run (missing project, tool not
/// found, etc.) counts as failed, same as one with actual failing test cases.</summary>
public sealed record UnitTestOrchestratorResult(bool Passed, IReadOnlyList<SuiteRunSummary> Suites, string? ReportPath);

/// <summary>
/// Runs every configured test suite for a project, one at a time, streaming each suite's own output
/// through the same <see cref="IOutputSink"/> live as it happens -- used both by <see cref="Publisher"/>
/// (as part of an optional publish step) and by the Projects tab's on-demand "Run Tests" button. This
/// is the one place suite results get aggregated into a single pass/fail and combined into one
/// report; individual <see cref="IUnitTestRunner"/> implementations only ever know about one suite
/// at a time.
///
/// Never throws -- a missing test project, a tool that can't be found, a non-zero exit code, or a
/// parse failure for any one suite are all just logged as a warning and counted as that suite
/// failing, since unit tests are purely informational and must never affect whether a publish
/// succeeds (see <see cref="IUnitTestRunner"/>'s remarks). The "Run Tests" button surfaces the
/// aggregate result instead of throwing UI errors for the same reason -- a red "Failed" in the grid
/// says everything a thrown exception would, without an alarming dialog.
/// </summary>
public static class UnitTestOrchestrator
{
    /// <summary>Null if this project type has no test runner at all, or the project has no suites
    /// configured -- the caller's cue to skip cleanly (no report, no status update) rather than
    /// treat it as a failure.</summary>
    public static async Task<UnitTestOrchestratorResult?> RunAllAsync(
        ProjectConfig project, string workingDir, IOutputSink output, CancellationToken ct, string? msBuildPath = null)
    {
        var runner = UnitTestRunnerRegistry.Get(project.ProjectType);
        var suites = runner?.GetConfiguredSuites(project) ?? Array.Empty<TestSuiteConfig>();
        if (runner is null || suites.Count == 0)
        {
            return null;
        }

        var allCases = new List<UnitTestCaseResult>();
        var summaries = new List<SuiteRunSummary>();
        var overallPassed = true;

        foreach (var suite in suites)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var suiteWorkingDir = Path.Combine(workingDir, SanitizeForPath(suite.Name));
                var result = await runner.RunSuiteAsync(project, suite, new UnitTestContext(suiteWorkingDir, output, msBuildPath), ct);
                var stampedCases = result.Cases.Select(c => c with { SuiteName = suite.Name }).ToList();
                allCases.AddRange(stampedCases);

                var passedCount = stampedCases.Count(c => string.Equals(c.Outcome, "Passed", StringComparison.OrdinalIgnoreCase));
                var failedCount = stampedCases.Count(c => string.Equals(c.Outcome, "Failed", StringComparison.OrdinalIgnoreCase));
                var skippedCount = stampedCases.Count - passedCount - failedCount;
                var suitePassed = result.Passed && failedCount == 0;
                summaries.Add(new SuiteRunSummary(suite.Name, suitePassed, stampedCases.Count, passedCount, failedCount, skippedCount));
                overallPassed &= suitePassed;

                output.Info(stampedCases.Count == 0
                    ? $"{suite.Name}: ran but produced no test cases to report."
                    : $"{suite.Name}: {passedCount}/{stampedCases.Count} passed" +
                      (failedCount > 0 ? $" ({failedCount} failed)" : "") +
                      (skippedCount > 0 ? $" ({skippedCount} skipped)" : "") + ".");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                output.Warn($"{suite.Name} could not be run: {ex.Message}");
                summaries.Add(new SuiteRunSummary(suite.Name, false, 0, 0, 0, 0));
                overallPassed = false;
            }
        }

        string? reportPath = null;
        if (allCases.Count > 0)
        {
            reportPath = Path.Combine(workingDir, "test-results.xlsx");
            UnitTestReportWriter.Write(allCases, reportPath);
        }

        return new UnitTestOrchestratorResult(overallPassed, summaries, reportPath);
    }

    /// <summary>Turns a suite name into something safe to use as a folder name -- shared with
    /// <see cref="Publisher"/>'s test-bundle step (which creates a subfolder per suite inside the
    /// zip) and <c>PublishTool.Hosting</c>'s remote test runner (which needs to reconstruct that same
    /// subfolder name after extracting), so both sides of that convention can never drift apart.</summary>
    public static string SanitizeForPath(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
