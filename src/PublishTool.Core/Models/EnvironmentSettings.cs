namespace PublishTool.Core.Models;

/// <summary>
/// The user-defined list of deployment environment names (e.g. "Staging", "Production",
/// "Development") offered when configuring a project's <see cref="DeploymentEnvironment"/> entries
/// or picking one to publish/deploy to. See <see cref="Services.IEnvironmentRegistry"/> for where
/// this is actually stored (local file vs shared via the dev server).
/// </summary>
public sealed class EnvironmentSettings
{
    public List<string> Names { get; set; } = new();

    /// <summary>Pre-selected when adding an environment to a project, or when nothing else has been
    /// chosen on the Publish tab. Null means no default is set yet.</summary>
    public string? DefaultName { get; set; }
}
