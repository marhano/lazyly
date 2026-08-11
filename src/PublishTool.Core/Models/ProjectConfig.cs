namespace PublishTool.Core.Models;

public sealed class ProjectConfig
{
    public required string Name { get; set; }

    public required string CsprojPath { get; set; }

    public required string PubxmlName { get; set; }

    public string? AssemblyInfoPath { get; set; }

    public required string IisHostPath { get; set; }

    /// <summary>
    /// Extra MSBuild targets (semicolon-separated) to force alongside the default build/publish
    /// target, for projects whose package .targets files don't hook into this MSBuild toolset's
    /// publish pipeline on their own (e.g. "CollectSQLiteInteropFiles" for older SQLite packages).
    /// </summary>
    public string? ExtraPublishTargets { get; set; }

    /// <summary>
    /// When true, publish ensures an IIS site named after this project exists before mirroring
    /// files into <see cref="IisHostPath"/> -- creating one with <see cref="IisBindings"/> if
    /// it's not already there. Never modifies an existing site.
    /// </summary>
    public bool AutoCreateIisSite { get; set; }

    public List<IisBinding> IisBindings { get; set; } = new();
}
