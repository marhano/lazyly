namespace PublishTool.Core.Models;

/// <summary>
/// The local-only counterpart to <see cref="SharedProjectConfig"/>, kept in a small per-dev file
/// (see <see cref="Services.RemoteProjectRegistry"/>) and never sent to the Remote Build Hosting
/// API -- facts about this one dev's own machine (where their clone of the repo lives, their own
/// local IIS target) plus per-user automation preferences. Fields are nullable/default rather than
/// required: a project shared by a teammate may not have local overrides configured on this
/// machine yet until this dev opens it and fills them in.
/// </summary>
public sealed class LocalProjectOverrides
{
    public string? CsprojPath { get; set; }

    public string? AssemblyInfoPath { get; set; }

    /// <summary>See <see cref="AngularProjectSettings.ProjectRootPath"/> -- local for the same
    /// reason <see cref="CsprojPath"/> is.</summary>
    public string? AngularProjectRootPath { get; set; }

    /// <summary>See <see cref="AndroidProjectSettings.ProjectRootPath"/>.</summary>
    public string? AndroidProjectRootPath { get; set; }

    /// <summary>See <see cref="AndroidProjectSettings.KeystorePath"/>.</summary>
    public string? AndroidKeystorePath { get; set; }

    /// <summary>See <see cref="AndroidProjectSettings.KeyAlias"/>.</summary>
    public string? AndroidKeyAlias { get; set; }

    /// <summary>DPAPI-bound to this Windows user, same reasoning as <see cref="EventLogProtectedPassword"/>.</summary>
    public string? AndroidProtectedKeystorePassword { get; set; }

    /// <summary>DPAPI-bound to this Windows user, same reasoning as <see cref="EventLogProtectedPassword"/>.</summary>
    public string? AndroidProtectedKeyPassword { get; set; }

    public bool LocalIisEnabled { get; set; }

    /// <summary>This dev's own named deploy targets -- see <see cref="ProjectConfig.LocalEnvironments"/>.</summary>
    public List<DeploymentEnvironment> LocalEnvironments { get; set; } = new();

    public bool RemoteIisEnabled { get; set; }

    public string? AppConfigPath { get; set; }

    /// <summary>DPAPI-bound to this Windows user -- can never be shared, even though
    /// <see cref="SharedProjectConfig.EventLogName"/>'s machine/username fields are shared.</summary>
    public string? EventLogProtectedPassword { get; set; }
}
