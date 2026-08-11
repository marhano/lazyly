namespace PublishTool.Core;

public interface IOutputSink
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);

    /// <summary>
    /// A high-level phase change (e.g. "Running MSBuild publish...") as opposed to a raw
    /// log line. Distinct from Info so the GUI can drive a status indicator off just the
    /// phases, without it flickering through every line of subprocess output.
    /// </summary>
    void Stage(string message);

    /// <summary>
    /// A standalone completion notification (e.g. a finished publish), distinct from Stage/Info
    /// because it should survive after the app loses focus -- the GUI shows this as an OS-level
    /// notification. <paramref name="filePath"/>, if given, is what clicking the notification reveals.
    /// </summary>
    void Notify(string title, string message, string? filePath = null);
}
