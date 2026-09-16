using PublishTool.Core.Models;

namespace PublishTool.Core.Services.UnitTestRunners;

/// <summary>
/// Every supported project type's unit test runner, keyed by <see cref="ProjectType"/>. Add a new
/// type by implementing <see cref="IUnitTestRunner"/> and adding a case below -- mirrors
/// <see cref="BuildRunners.BuildRunnerRegistry"/>, except a runner isn't necessarily 1:1 with a
/// <see cref="ProjectType"/> (Angular and Android share <see cref="JsUnitTestRunner"/>, since both
/// are ultimately "run npm test in the project root"), so this switches directly instead of
/// scanning a list by a declared type.
/// </summary>
public static class UnitTestRunnerRegistry
{
    private static readonly DotNetUnitTestRunner DotNetRunner = new();
    private static readonly JsUnitTestRunner JsRunner = new();

    /// <summary>Null for a project type with no unit test story at all.</summary>
    public static IUnitTestRunner? Get(ProjectType type) => type switch
    {
        ProjectType.DotNet => DotNetRunner,
        ProjectType.Angular or ProjectType.Android => JsRunner,
        _ => null,
    };
}
