using System.Text.Json;
using System.Text.Json.Serialization;

namespace PublishTool.Core.Models;

/// <summary>
/// The team-wide subset of <see cref="AngularProjectSettings"/> -- everything except
/// <see cref="AngularProjectSettings.ProjectRootPath"/>, which is local to each dev's own checkout
/// (see <see cref="LocalProjectOverrides.AngularProjectRootPath"/>), same split as
/// <see cref="ProjectConfig.CsprojPath"/> vs <see cref="SharedProjectConfig.PubxmlName"/>.
/// </summary>
public sealed class SharedAngularProjectSettings
{
    public string? WorkspaceProjectName { get; set; }

    /// <summary>See <see cref="SharedProjectConfig.ExtensionData"/> -- kept here too so a future
    /// shared Angular-specific field nested under "angular" round-trips through an old server the
    /// same way a new top-level field does.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
