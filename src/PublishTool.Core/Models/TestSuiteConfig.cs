namespace PublishTool.Core.Models;

/// <summary>One named test suite for a project, e.g. "Unit Test" or "E2E" -- the merged runtime shape
/// every <see cref="Services.UnitTestRunners.IUnitTestRunner"/>/<see cref="Services.Publisher"/>
/// bundling/reporting code actually consumes. Not stored directly on <see cref="ProjectConfig"/> --
/// it's computed (<see cref="ProjectConfig.TestSuites"/>) from <see cref="SharedTestSuiteConfig"/>
/// (which suite TYPES exist, shared team-wide) joined against <see cref="LocalTestSuiteConfig"/>
/// (where THIS dev's own copy of each lives). Kept as its own plain name+path shape so none of the
/// downstream code needed to change when that split was introduced.</summary>
public sealed class TestSuiteConfig
{
    /// <summary>This suite's display name, e.g. "Unit Test" or "E2E" -- see
    /// <see cref="TestSuiteTypeNames.DisplayName"/>, the one place this string is decided.</summary>
    public required string Name { get; set; }

    /// <summary>Path to the .NET test project (a *.Tests.csproj) to run via <c>dotnet test</c>.
    /// Only meaningful for <see cref="ProjectType.DotNet"/> -- Angular/Android projects don't use
    /// this field at all (see <see cref="Services.UnitTestRunners.JsUnitTestRunner"/>).</summary>
    public string? ProjectPath { get; set; }
}
