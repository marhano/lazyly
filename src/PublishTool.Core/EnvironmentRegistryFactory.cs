using PublishTool.Core.Services;

namespace PublishTool.Core;

/// <summary>
/// Chooses between <see cref="LocalEnvironmentRegistry"/> and <see cref="RemoteEnvironmentRegistry"/>
/// based on <see cref="AppSettings.UseRemoteMode"/> -- same decision, same reasoning, as
/// <see cref="ProjectRegistryFactory"/>, just for the Settings tab's environment name list instead
/// of the project registry.
/// </summary>
public static class EnvironmentRegistryFactory
{
    public static IEnvironmentRegistry Create()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        if (settings.UseRemoteMode && !string.IsNullOrWhiteSpace(settings.RemoteHostingUrl))
        {
#pragma warning disable CA1416 // DPAPI (SecretProtector) is Windows-only; this whole tool (MSBuild, IIS, appcmd) only ever runs on Windows despite PublishTool.Core's plain net8.0 TFM.
            var apiKey = settings.RemoteHostingProtectedApiKey is null
                ? null
                : SecretProtector.TryUnprotect(settings.RemoteHostingProtectedApiKey, SecretProtector.RemoteHostingPurpose);
#pragma warning restore CA1416

            return new RemoteEnvironmentRegistry(settings.RemoteHostingUrl, apiKey);
        }

        return new LocalEnvironmentRegistry(LocalEnvironmentRegistry.DefaultPath);
    }
}
