namespace PublishTool.Core.Models;

public sealed class PublishOptions
{
    public required string ProjectName { get; set; }

    public required string Version { get; set; }

    public required string BuildsRoot { get; set; }

    public string? MsBuildPath { get; set; }

    /// <summary>Git branch to check out before building, if set. The project must be inside a
    /// git working tree; left null, publish builds whatever's currently checked out.</summary>
    public string? GitBranch { get; set; }

    public List<string> ReleaseNotesFeatures { get; set; } = new();

    public List<string> ReleaseNotesFixes { get; set; } = new();

    public List<string> ReleaseNotesOtherUpdates { get; set; } = new();

    public List<string> ReleaseNotesBacklogItems { get; set; } = new();

    /// <summary>App-config key/value settings to write into the project's config file before
    /// building, if the project uses app config (see ProjectConfig.UseAppConfig). Null/empty
    /// means "don't touch the config file" -- distinct from writing an empty dictionary.</summary>
    public Dictionary<string, string>? AppConfigSettings { get; set; }
}
