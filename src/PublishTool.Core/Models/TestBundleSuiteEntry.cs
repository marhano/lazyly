namespace PublishTool.Core.Models;

/// <summary>One test suite as it exists inside a build's <see cref="BuildManifest.TestBundlePath"/>
/// zip -- <see cref="Name"/> matches the suite's name in <see cref="ProjectConfig.TestSuites"/>, and
/// <see cref="AssemblyFileName"/> is the built test assembly's file name (e.g.
/// "MyProject.Tests.dll") inside that suite's own subfolder within the bundle, so the dev server
/// knows exactly which file to run without having to guess or search for it after extracting.</summary>
public sealed class TestBundleSuiteEntry
{
    public required string Name { get; set; }

    public required string AssemblyFileName { get; set; }
}
