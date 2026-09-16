namespace PublishTool.Core.Models;

/// <summary>One suite's outcome within a <see cref="TestRunStatus"/> -- e.g. "Unit Test: 12/12
/// passed" or "E2E Test: 8/10 passed (2 failed)".</summary>
public sealed class TestSuiteRunStatus
{
    public required string SuiteName { get; set; }

    public required bool Passed { get; set; }

    public int TotalCount { get; set; }

    public int PassedCount { get; set; }

    public int FailedCount { get; set; }

    public int SkippedCount { get; set; }
}

/// <summary>The most recent test run result for one build (a specific <see cref="ProjectName"/> +
/// <see cref="Version"/> pair) -- overwritten each time "Run Tests" is used again for that same
/// build, not an append log; see <see cref="Services.TestRunStatusStore"/>. Test status is
/// per-BUILD, not per-project: running tests for one version says nothing about whether a
/// differently-versioned build (built from different source) would pass, so each build's row in the
/// build-history grid tracks and shows its own result. <see cref="Passed"/> is the aggregate across
/// every suite in <see cref="Suites"/> -- true only if every suite passed.</summary>
public sealed class TestRunStatus
{
    public required string ProjectName { get; set; }

    public required string Version { get; set; }

    public required bool Passed { get; set; }

    public required DateTimeOffset RunAtUtc { get; set; }

    public required string PerformedBy { get; set; }

    public List<TestSuiteRunStatus> Suites { get; set; } = new();
}
