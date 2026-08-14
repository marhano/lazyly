namespace PublishTool.Core.Models;

/// <summary>
/// One entry in an IIS site's deployment history, recorded by <see cref="Services.BuildDeployer"/>
/// after every successful deploy and read back by <see cref="Services.IisSiteManager"/> for the IIS
/// tab's "deployed version/date/by" columns and full history view. Stored by
/// <see cref="Services.SiteDeploymentStore"/> -- never inside the site's own web root, since IIS
/// serves static files by default and this would otherwise be readable by anyone who guessed the
/// filename.
/// </summary>
public sealed class SiteDeploymentRecord
{
    public required string SiteName { get; set; }

    public required string ProjectName { get; set; }

    public required string Version { get; set; }

    public required string EnvironmentName { get; set; }

    public required DateTimeOffset DeployedAtUtc { get; set; }

    public required string DeployedBy { get; set; }
}
