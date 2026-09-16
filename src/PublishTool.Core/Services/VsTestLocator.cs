using System.Diagnostics;

namespace PublishTool.Core.Services;

/// <summary>
/// Resolves vstest.console.exe for running classic (non-SDK-style) .NET Framework test projects --
/// see <see cref="UnitTestRunners.DotNetUnitTestRunner"/> for why "dotnet test" can't run these.
/// </summary>
public static class VsTestLocator
{
    /// <summary>Checked before vswhere -- <c>PublishTool.Hosting</c> bundles a portable,
    /// xcopy-deployable vstest.console.exe (via a Microsoft.TestPlatform package reference copied
    /// into its own output at "vstest-portable\") specifically so the dev server never needs Visual
    /// Studio/Build Tools installed at all for remote test execution. A dev's own machine running the
    /// GUI never has this folder (only Hosting's csproj bundles it), so local runs are unaffected and
    /// still resolve via vswhere below, same as always.</summary>
    private const string BundledRelativePath = "vstest-portable\\vstest.console.exe";

    public static async Task<string?> LocateAsync(CancellationToken ct = default)
    {
        var bundledPath = Path.Combine(AppContext.BaseDirectory, BundledRelativePath);
        if (File.Exists(bundledPath))
        {
            return bundledPath;
        }

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
        // Same filter MsBuildLocator uses -- without it, "-latest" can match some other VS-installer
        // product (e.g. SQL Server Management Studio) that has no vstest.console.exe at all.
        psi.ArgumentList.Add("-requires");
        psi.ArgumentList.Add("Microsoft.Component.MSBuild");
        psi.ArgumentList.Add("-find");
        psi.ArgumentList.Add(@"Common7\IDE\**\vstest.console.exe");

        using var process = Process.Start(psi);
        if (process is null)
        {
            return null;
        }

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => File.Exists(line));
    }
}
