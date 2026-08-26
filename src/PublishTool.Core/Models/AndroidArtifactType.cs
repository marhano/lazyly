namespace PublishTool.Core.Models;

/// <summary>Which artifact a <see cref="ProjectType.Android"/> project's release build produces.</summary>
public enum AndroidArtifactType
{
    Apk = 0,
    Aab = 1,
}
