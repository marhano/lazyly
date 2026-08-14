namespace PublishTool.Core.Models;

/// <summary>Which side of a project's deploy targets a publish (or manual redeploy) goes to --
/// exactly one, never both at once, since local and remote are fundamentally different mechanisms
/// (an instant local file copy vs. an HTTP call to the dev server) that a user picks between
/// explicitly rather than having inferred from environment-name overlap.</summary>
public enum DeployTarget
{
    None,
    Local,
    Remote,
}

public sealed class PublishOptions
{
    public required string ProjectName { get; set; }

    public required string Version { get; set; }

    public required string BuildsRoot { get; set; }

    /// <summary>Flags this build as the project's "latest release" on the hosting site once
    /// published, un-flagging whatever build previously held that for the same project. See
    /// <see cref="Services.BuildRepository.SetLatest"/>.</summary>
    public bool MarkAsLatest { get; set; }

    /// <summary>Base URL of the Remote Build Hosting API, e.g. "https://devserver.internal". Only
    /// used when <see cref="UseRemoteMode"/> is true -- supplied so <see cref="Services.Publisher"/>
    /// doesn't need to load <see cref="AppSettings"/> itself.</summary>
    public string? RemoteHostingUrl { get; set; }

    /// <summary>Plaintext API key for <see cref="RemoteHostingUrl"/>, already decrypted by the
    /// caller (see <see cref="AppSettings.RemoteHostingProtectedApiKey"/>) -- <see cref="Publisher"/>
    /// itself never touches DPAPI directly.</summary>
    public string? RemoteHostingApiKey { get; set; }

    /// <summary>Mirrors <see cref="AppSettings.UseRemoteMode"/> at the moment this publish started
    /// (resolved by the caller, same reasoning as <see cref="RemoteHostingUrl"/>). When true, the
    /// build is never archived to <see cref="BuildsRoot"/> at all -- it's built straight into a
    /// throwaway staging location and uploaded to <see cref="RemoteHostingUrl"/> instead, so every
    /// dev's local machine doesn't accumulate its own redundant copy of every team build. Local IIS
    /// deployment (see <see cref="DeployEnvironmentName"/>) is independent of this -- a dev can
    /// still deploy to their own local IIS for testing while in remote mode.</summary>
    public bool UseRemoteMode { get; set; }

    /// <summary>Which side (local IIS or the dev server) this publish deploys to, if either. Gates
    /// <see cref="DeployEnvironmentName"/> -- <see cref="DeployTarget.None"/> means archive/upload
    /// only, no deploy at all.</summary>
    public DeployTarget DeployTarget { get; set; } = DeployTarget.None;

    /// <summary>Which named environment, within whichever list <see cref="DeployTarget"/> selects
    /// (<see cref="ProjectConfig.LocalEnvironments"/> or <see cref="ProjectConfig.RemoteEnvironments"/>),
    /// this publish deploys to. Ignored when <see cref="DeployTarget"/> is <see cref="DeployTarget.None"/>.</summary>
    public string? DeployEnvironmentName { get; set; }

    public string? MsBuildPath { get; set; }

    /// <summary>Git branch to check out before building, if set. The project must be inside a
    /// git working tree; left null, publish builds whatever's currently checked out.</summary>
    public string? GitBranch { get; set; }

    public List<string> ReleaseNotesFeatures { get; set; } = new();

    public List<string> ReleaseNotesFixes { get; set; } = new();

    public List<string> ReleaseNotesOtherUpdates { get; set; } = new();

    public List<string> ReleaseNotesBacklogItems { get; set; } = new();

    /// <summary>App-config key/value settings to write into the project's config file before
    /// building, if the project uses app config (see ProjectConfig.UseAppConfig). Null/empty
    /// means "don't touch the config file" -- distinct from writing an empty dictionary.</summary>
    public Dictionary<string, string>? AppConfigSettings { get; set; }
}
