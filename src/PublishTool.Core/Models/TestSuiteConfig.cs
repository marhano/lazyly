namespace PublishTool.Core.Models;

/// <summary>One named test suite for a project -- e.g. "Unit Test" and "E2E Test" as two separate
/// entries, each with its own test project. Deliberately a list rather than a fixed pair of fields,
/// so adding a third kind later (or renaming/removing one) is just editing this list, no schema
/// change. Local to this machine, same reasoning as <see cref="ProjectConfig.CsprojPath"/> -- a
/// filesystem path isn't something every teammate's checkout shares, so the whole list lives in
/// <see cref="LocalProjectOverrides"/>, not <see cref="SharedProjectConfig"/>.</summary>
public sealed class TestSuiteConfig
{
    /// <summary>Free-text label, e.g. "Unit Test" or "E2E Test" -- also this suite's identity within
    /// the project (used to match it up across saves), so it should stay unique per project.</summary>
    public required string Name { get; set; }

    /// <summary>Path to the .NET test project (a *.Tests.csproj) to run via <c>dotnet test</c>.
    /// Only meaningful for <see cref="ProjectType.DotNet"/> -- Angular/Android projects don't use
    /// this field at all (see <see cref="Services.UnitTestRunners.JsUnitTestRunner"/>).</summary>
    public string? ProjectPath { get; set; }
}
