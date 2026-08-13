using System.Text.Json;

namespace PublishTool.Core;

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string BuildsRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PublishTool",
        "Builds");

    /// <summary>Explicit path to MSBuild.exe. When null, it's auto-discovered via vswhere.</summary>
    public string? MsBuildPath { get; set; }

    /// <summary>"Light" or "Dark" for an explicit user choice; null follows the OS theme.</summary>
    public string? Theme { get; set; }

    /// <summary>Hex accent color (from <see cref="AccentPresets"/>); null uses WPF-UI's default.</summary>
    public string? AccentColor { get; set; }

    /// <summary>Base URL of a Remote Build Hosting API (a PublishTool.Hosting instance's
    /// <c>/api/builds</c> surface), for publishing straight to a dev server this machine has no
    /// filesystem access to. Null/empty means the feature is unconfigured -- the Publish tab's
    /// "Also upload to remote hosting" toggle is disabled in that case.</summary>
    public string? RemoteHostingUrl { get; set; }

    /// <summary>DPAPI-protected (current-user-scoped, <see cref="Services.SecretProtector.RemoteHostingPurpose"/>)
    /// API key for <see cref="RemoteHostingUrl"/>. Never stored in plain text.</summary>
    public string? RemoteHostingProtectedApiKey { get; set; }

    /// <summary>When true: the Projects tab and IIS tab read from/write to the dev server
    /// (<see cref="RemoteHostingUrl"/>) instead of this machine's local project registry/IIS -- see
    /// <see cref="Services.ProjectRegistryFactory"/> -- and every publish uploads straight to the
    /// dev server instead of archiving to this machine's local BuildsRoot at all, see
    /// <see cref="Services.Publisher"/>. Local IIS deployment
    /// (<see cref="Models.ProjectConfig.LocalIisDeploymentEnabled"/>) and dev-server auto-deploy
    /// (<see cref="Models.ProjectConfig.AutoDeployOnPublish"/>) remain independent per-project
    /// choices on top of this.</summary>
    public bool UseRemoteMode { get; set; }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PublishTool",
        "settings.json");

    public static AppSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new AppSettings();
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
