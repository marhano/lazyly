namespace PublishTool.Gui;

/// <summary>One row in the Export/Import projects dialogs' checklist.</summary>
public sealed class SelectableProjectViewModel
{
    public required string Name { get; init; }

    public bool IsChecked { get; set; }

    /// <summary>Shown next to the name in the Import dialog only, e.g. "New" or "Overwrites
    /// existing project". Null in the Export dialog, which has nothing to warn about.</summary>
    public string? StatusText { get; init; }
}
