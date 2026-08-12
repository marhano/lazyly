namespace PublishTool.Core.Models;

public sealed class PublishOptions
{
    public required string ProjectName { get; set; }

    public required string Version { get; set; }

    public required string BuildsRoot { get; set; }

    public string? MsBuildPath { get; set; }

    public List<string> ReleaseNotesFeatures { get; set; } = new();

    public List<string> ReleaseNotesFixes { get; set; } = new();

    public List<string> ReleaseNotesOtherUpdates { get; set; } = new();

    public List<string> ReleaseNotesBacklogItems { get; set; } = new();
}
