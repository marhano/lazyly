namespace PublishTool.Core.Models;

/// <summary>The per-dev half of a project's test suite configuration -- WHERE, on this machine, the
/// test project for a given <see cref="TestSuiteType"/> lives. Paired against
/// <see cref="SharedTestSuiteConfig"/> by <see cref="Type"/> to build the merged
/// <see cref="ProjectConfig.TestSuites"/> list every runner actually consumes. A type declared in
/// Shared settings with no matching entry here yet (a teammate added the type, this dev hasn't
/// pointed it at their own checkout) simply has a null <see cref="ProjectPath"/> once merged.</summary>
public sealed class LocalTestSuiteConfig
{
    public required TestSuiteType Type { get; set; }

    public string? ProjectPath { get; set; }
}
