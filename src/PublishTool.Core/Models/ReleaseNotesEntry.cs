namespace PublishTool.Core.Models;

public sealed class ReleaseNotesEntry
{
    public required string Reference { get; set; }

    public required string Version { get; set; }

    public required DateOnly Date { get; set; }

    public List<string> Features { get; set; } = new();

    public List<string> Fixes { get; set; } = new();

    public List<string> OtherUpdates { get; set; } = new();

    public List<string> BacklogItems { get; set; } = new();
}
