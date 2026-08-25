using PublishTool.Core.Models;

namespace PublishTool.Core.Services.BuildRunners;

/// <summary>
/// Every supported project type, keyed by <see cref="IBuildRunner.ProjectType"/>. Add a new type by
/// implementing <see cref="IBuildRunner"/> and listing it here.
/// </summary>
public static class BuildRunnerRegistry
{
    public static readonly IReadOnlyList<IBuildRunner> All = new IBuildRunner[]
    {
        new DotNetBuildRunner(),
        new AngularBuildRunner(),
        new AndroidBuildRunner(),
    };

    public static IBuildRunner Get(ProjectType type) =>
        All.FirstOrDefault(r => r.ProjectType == type)
            ?? throw new InvalidOperationException($"No build runner registered for project type '{type}'.");
}
