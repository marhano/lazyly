using PublishTool.Core.Models;

namespace PublishTool.Core.Services;

/// <summary>Checks for the external tools PublishTool shells out to (MSBuild, IIS's appcmd),
/// so missing-dependency failures can be surfaced up front instead of only at publish time.</summary>
public static class DependencyChecker
{
    public static async Task<IReadOnlyList<DependencyCheckResult>> CheckAllAsync(
        string? configuredMsBuildPath, CancellationToken ct = default)
    {
        var results = new List<DependencyCheckResult>
        {
            await CheckMsBuildAsync(configuredMsBuildPath, ct),
            CheckIis(),
        };

        return results;
    }

    private static async Task<DependencyCheckResult> CheckMsBuildAsync(string? configuredMsBuildPath, CancellationToken ct)
    {
        try
        {
            var path = await MsBuildLocator.LocateAsync(configuredMsBuildPath, ct);
            return new DependencyCheckResult { Name = "MSBuild", IsAvailable = true, Details = path };
        }
        catch (Exception ex)
        {
            return new DependencyCheckResult { Name = "MSBuild", IsAvailable = false, Details = ex.Message };
        }
    }

    private static DependencyCheckResult CheckIis()
    {
        var appCmdPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "inetsrv", "appcmd.exe");
        var exists = File.Exists(appCmdPath);

        return new DependencyCheckResult
        {
            Name = "IIS (appcmd.exe)",
            IsAvailable = exists,
            Details = exists ? appCmdPath : "Not found -- IIS doesn't appear to be installed on this machine.",
        };
    }
}
