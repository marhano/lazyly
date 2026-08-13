using PublishTool.Core.Services;

namespace PublishTool.Core;

/// <summary>
/// Chooses between the local <see cref="ProjectRegistry"/> and <see cref="Services.RemoteProjectRegistry"/>
/// based on <see cref="AppSettings.UseRemoteMode"/> -- the single place every caller (GUI, CLI) asks
/// for "the current project registry" instead of constructing <see cref="ProjectRegistry"/> directly,
/// so this decision lives in one place.
/// </summary>
public static class ProjectRegistryFactory
{
    public static IProjectRegistry Create()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        if (settings.UseRemoteMode && !string.IsNullOrWhiteSpace(settings.RemoteHostingUrl))
        {
#pragma warning disable CA1416 // DPAPI (SecretProtector) is Windows-only; this whole tool (MSBuild, IIS, appcmd) only ever runs on Windows despite PublishTool.Core's plain net8.0 TFM.
            var apiKey = settings.RemoteHostingProtectedApiKey is null
                ? null
                : SecretProtector.TryUnprotect(settings.RemoteHostingProtectedApiKey, SecretProtector.RemoteHostingPurpose);
#pragma warning restore CA1416

            return new RemoteProjectRegistry(settings.RemoteHostingUrl, apiKey, RemoteProjectRegistry.DefaultLocalOverridesPath);
        }

        return new ProjectRegistry(ProjectRegistry.DefaultPath);
    }
}
