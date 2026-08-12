namespace PublishTool.Core.Services.AppConfig;

/// <summary>
/// Every supported app-config format, keyed by <see cref="IAppConfigProvider.TypeName"/>. Add a
/// new format by implementing <see cref="IAppConfigProvider"/> and listing it here -- the GUI's
/// config-type dropdown and the CLI's validation are both driven off this list, nothing else to wire up.
/// </summary>
public static class AppConfigProviderRegistry
{
    public static readonly IReadOnlyList<IAppConfigProvider> All = new IAppConfigProvider[]
    {
        new WebConfigAppSettingsProvider(),
    };

    public static IAppConfigProvider? Get(string? typeName) =>
        typeName is null
            ? null
            : All.FirstOrDefault(p => string.Equals(p.TypeName, typeName, StringComparison.OrdinalIgnoreCase));
}
