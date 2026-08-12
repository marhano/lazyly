namespace PublishTool.Core.Services.AppConfig;

/// <summary>
/// Reads and writes a project's user-visible app settings (e.g. a version number shown in the
/// UI, or any other appSettings-style key/value) directly on disk, so they can be changed from
/// PublishTool without opening the project in an IDE. One implementation per config file format
/// -- <see cref="AppConfigProviderRegistry"/> is the extension point for adding more later.
/// </summary>
public interface IAppConfigProvider
{
    /// <summary>Stable identifier stored on the project (ProjectConfig.AppConfigType) and in
    /// build manifests -- never change this for an existing provider, it'd orphan saved settings.</summary>
    string TypeName { get; }

    /// <summary>Shown in the GUI's config-type dropdown.</summary>
    string DisplayName { get; }

    /// <summary>Reads the current key/value settings from the config file at <paramref name="configPath"/>.</summary>
    Dictionary<string, string> ReadSettings(string configPath);

    /// <summary>Writes <paramref name="settings"/> into the config file at <paramref name="configPath"/>,
    /// updating existing keys and adding any that don't already exist. Never removes keys that
    /// aren't present in <paramref name="settings"/> -- this only ever touches what it's given.</summary>
    void WriteSettings(string configPath, IReadOnlyDictionary<string, string> settings);
}
