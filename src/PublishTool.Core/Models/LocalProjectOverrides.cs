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

    public bool LocalIisDeploymentEnabled { get; set; }

    public string? IisHostPath { get; set; }

    public List<IisBinding> IisBindings { get; set; } = new();

    public bool AutoCreateIisSite { get; set; }

    public string? AppConfigPath { get; set; }

    /// <summary>DPAPI-bound to this Windows user -- can never be shared, even though
    /// <see cref="SharedProjectConfig.EventLogName"/>'s machine/username fields are shared.</summary>
    public string? EventLogProtectedPassword { get; set; }

    public bool AutoDeployOnPublish { get; set; }
}
