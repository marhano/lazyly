using System.Diagnostics;

namespace PublishTool.Core.Services;

/// <summary>
/// Resolves the MSBuild.exe to use for publishing classic .NET Framework projects.
/// dotnet SDK's own MSBuild lacks the Web Publishing targets, so this must be the
/// full MSBuild that ships with Visual Studio / Build Tools.
/// </summary>
public static class MsBuildLocator
{
    public static async Task<string> LocateAsync(string? configuredPath, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (!File.Exists(configuredPath))
            {
                throw new FileNotFoundException("Configured MSBuild path does not exist. Fix it with 'set-msbuild-path'.", configuredPath);
            }

            return configuredPath;
        }

        var found = await LocateViaVsWhereAsync(ct);
        if (found is not null)
        {
            return found;
        }

        throw new InvalidOperationException(
            "Could not locate MSBuild.exe via vswhere, and no MSBuild path is configured. " +
            "Install Visual Studio (or Build Tools) with the ASP.NET/web development workload, " +
            "or set an explicit path with 'set-msbuild-path --path <path-to-MSBuild.exe>'.");
    }

    private static async Task<string?> LocateViaVsWhereAsync(CancellationToken ct)
    {
        var vswherePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio",
            "Installer",
            "vswhere.exe");

        if (!File.Exists(vswherePath))
        {
            return null;
        }

        var psi = new ProcessStartInfo(vswherePath)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-latest");
        psi.ArgumentList.Add("-prerelease");
        psi.ArgumentList.Add("-products");
        psi.ArgumentList.Add("*");
        psi.ArgumentList.Add("-requires");
        psi.ArgumentList.Add("Microsoft.Component.MSBuild");
        psi.ArgumentList.Add("-find");
        psi.ArgumentList.Add(@"MSBuild\**\Bin\MSBuild.exe");

        using var process = Process.Start(psi);
        if (process is null)
        {
            return null;
        }

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var path = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => File.Exists(line));

        return path;
    }
}
