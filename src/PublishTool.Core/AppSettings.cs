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
