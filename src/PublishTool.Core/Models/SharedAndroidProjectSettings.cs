using System.Text.Json;
using System.Text.Json.Serialization;

namespace PublishTool.Core.Models;

/// <summary>
/// The team-wide subset of <see cref="AndroidProjectSettings"/> -- empty, since the only field left
/// there (<see cref="AndroidProjectSettings.ProjectRootPath"/>) is local to each dev's own checkout
/// (see <see cref="LocalProjectOverrides.AndroidProjectRootPath"/>). Still exists, rather than
/// dropping <see cref="SharedProjectConfig.Android"/> entirely, so its non-null-ness keeps meaning
/// "this shared project record declares itself Android" independent of any one dev's local setup.
/// </summary>
public sealed class SharedAndroidProjectSettings
{
    /// <summary>See <see cref="SharedProjectConfig.ExtensionData"/> -- kept here too so a future
    /// shared Android-specific field nested under "android" round-trips through an old server the
    /// same way a new top-level field does.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
