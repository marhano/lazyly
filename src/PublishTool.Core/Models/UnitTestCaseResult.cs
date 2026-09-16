namespace PublishTool.Core.Models;

/// <summary>One test case's outcome, in a shape shared by every test framework's result format
/// this tool parses (TRX for .NET, JUnit XML for Angular/Android's JS test runners) -- see
/// <see cref="Services.UnitTestRunners.TrxTestResultParser"/> and
/// <see cref="Services.UnitTestRunners.JUnitXmlTestResultParser"/>.</summary>
public sealed record UnitTestCaseResult(
    string Name,
    string Outcome,
    TimeSpan Duration,
    string? ClassName,
    string? ErrorMessage)
{
    /// <summary>Which named <see cref="TestSuiteConfig"/> this case came from, e.g. "Unit Test" or
    /// "E2E Test" -- stamped on by the orchestrator after parsing (a parser has no notion of which
    /// suite it was given), not known at parse time. Empty for a single-suite run with no name to
    /// distinguish (e.g. a JS project's implicit suite).</summary>
    public string SuiteName { get; init; } = string.Empty;
}
