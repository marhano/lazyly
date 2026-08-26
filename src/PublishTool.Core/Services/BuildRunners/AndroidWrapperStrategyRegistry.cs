namespace PublishTool.Core.Services.BuildRunners;

/// <summary>
/// Every supported native wrapper, keyed by which marker file it detects in a project's root.
/// Add a new wrapper (e.g. a future bare React Native project) by implementing
/// <see cref="IAndroidWrapperStrategy"/> and listing it here -- no other code needs to change.
/// </summary>
public static class AndroidWrapperStrategyRegistry
{
    public static readonly IReadOnlyList<IAndroidWrapperStrategy> All = new IAndroidWrapperStrategy[]
    {
        new CapacitorWrapperStrategy(),
        new CordovaWrapperStrategy(),
    };

    public static IAndroidWrapperStrategy? Detect(string projectRoot) =>
        All.FirstOrDefault(s => s.Detect(projectRoot));
}
