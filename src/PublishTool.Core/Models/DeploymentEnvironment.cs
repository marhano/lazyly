namespace PublishTool.Core.Models;

/// <summary>
/// One named deployment target for a project -- e.g. "Staging" or "Production". A project can have
/// several of these in each of <see cref="ProjectConfig.LocalEnvironments"/> (this dev's own IIS)
/// and <see cref="ProjectConfig.RemoteEnvironments"/> (the dev server's IIS); which one a given
/// publish (or manual redeploy) actually uses is chosen at that time, not fixed on the project.
/// </summary>
public sealed class DeploymentEnvironment
{
    /// <summary>Must match one of the names in <see cref="EnvironmentSettings"/> -- the Settings tab
    /// is where environment names are defined; this just says "this project deploys to that one."</summary>
    public required string Name { get; set; }

    /// <summary>Root folder this environment's builds are mirrored under, e.g.
    /// "C:\PublishToolServerTest". The actual deploy path is always
    /// "{HostRootPath}\{project name}\{environment name}" -- see <see cref="ResolveHostPath"/> --
    /// so the same root can host every project/environment combination without collisions.</summary>
    public string? HostRootPath { get; set; }

    public List<IisBinding> Bindings { get; set; } = new();

    public bool AutoCreateSite { get; set; }

    /// <summary>The actual folder this environment's builds get mirrored into for
    /// <paramref name="projectName"/>, or null if <see cref="HostRootPath"/> isn't configured yet.</summary>
    public string? ResolveHostPath(string projectName) =>
        string.IsNullOrWhiteSpace(HostRootPath) ? null : Path.Combine(HostRootPath, projectName, Name);

    /// <summary>The IIS site (and application pool -- <see cref="Services.BuildDeployer"/> always
    /// uses the same name for both) this environment deploys <paramref name="projectName"/> under,
    /// e.g. "OmniPay Business - Production". Distinct per environment so multiple environments for
    /// the same project never collide on one IIS site/pool.</summary>
    public string ResolveSiteName(string projectName) => $"{projectName} - {Name}";
}
