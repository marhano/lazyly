namespace PublishTool.Gui;

/// <summary>A single editable key/value row in the Publish tab's App Config grid.</summary>
public sealed class AppConfigSettingRow
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}
