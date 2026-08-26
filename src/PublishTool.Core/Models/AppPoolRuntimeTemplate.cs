namespace PublishTool.Core.Models;

/// <summary>
/// Which managed-runtime template <see cref="Services.IisSiteManager"/> gives a newly auto-created
/// application pool. <see cref="DotNetFramework"/> is the existing default (classic .NET Framework
/// Web Deploy apps), unchanged for every project that existed before Angular support. Angular's
/// static-file output (and ASP.NET Core, which handles its own runtime via Kestrel/ANCM) wants
/// <see cref="NoManagedCode"/> instead -- IIS's CLR hosting is never used either way.
/// </summary>
public enum AppPoolRuntimeTemplate
{
    DotNetFramework = 0,
    NoManagedCode = 1,
}
