namespace PublishTool.Core.Models;

/// <summary>One suite's outcome within a <see cref="TestRunStatus"/> -- e.g. "Unit Test: 12/12
/// passed" or "E2E: 8/10 passed (2 failed)". Carries its own <see cref="RunAtUtc"/>/<see cref="PerformedBy"/>
/// (not just the parent's) because suites are now run and merged independently -- see
/// <see cref="TestRunStatus"/>'s remarks -- so a suite last run yesterday by one teammate and one run
/// just now by another must each show their own real timestamp, not the other's.</summary>
public sealed class TestSuiteRunStatus
{
    public required string SuiteName { get; set; }

    public required bool Passed { get; set; }

    public int TotalCount { get; set; }

    public int PassedCount { get; set; }

    public int FailedCount { get; set; }

    public int SkippedCount { get; set; }

    public DateTimeOffset RunAtUtc { get; set; }

    public string? PerformedBy { get; set; }
}

/// <summary>The most recent test run result for one build (a specific <see cref="ProjectName"/> +
/// <see cref="Version"/> pair) -- see <see cref="Services.TestRunStatusStore"/>. Test status is
/// per-BUILD, not per-project: running tests for one version says nothing about whether a
/// differently-versioned build (built from different source) would pass, so each build's row in the
/// build-history grid tracks and shows its own result. <see cref="Passed"/> is the aggregate across
/// every suite in <see cref="Suites"/> -- true only if every suite passed.
///
/// Unlike earlier, this is no longer wholesale-overwritten on every run: the Projects tab's test-run
/// dialog runs and views each suite type independently (see <see cref="Services.UnitTestRunners.UnitTestOrchestrator"/>'s
/// <c>suiteNameFilter</c>), so running just "E2E" merges only that suite's entry into
/// <see cref="Suites"/> -- "Unit Test"'s last-known entry (and its own <see cref="TestSuiteRunStatus.RunAtUtc"/>)
/// is left exactly as it was. <see cref="RunAtUtc"/>/<see cref="PerformedBy"/> here reflect whichever
/// run touched this status object most recently (any suite) -- for a specific suite's own timestamp,
/// read <see cref="TestSuiteRunStatus.RunAtUtc"/> instead.</summary>
public sealed class TestRunStatus
{
    public required string ProjectName { get; set; }

    public required string Version { get; set; }

    public required bool Passed { get; set; }

    public required DateTimeOffset RunAtUtc { get; set; }

    public required string PerformedBy { get; set; }

    public List<TestSuiteRunStatus> Suites { get; set; } = new();
}
