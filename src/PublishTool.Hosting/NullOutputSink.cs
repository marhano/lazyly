using PublishTool.Core;

namespace PublishTool.Hosting;

/// <summary>No-op <see cref="IOutputSink"/> for simple, fast Core service calls (e.g. listing IIS
/// sites/pools) where there's nothing worth logging beyond the HTTP request/response itself --
/// <see cref="LoggerOutputSink"/> is used instead wherever the underlying operation (like a
/// deploy) does enough that its own step-by-step log lines are actually useful.</summary>
internal sealed class NullOutputSink : IOutputSink
{
    public static readonly NullOutputSink Instance = new();

    private NullOutputSink()
    {
    }

    public void Info(string message)
    {
    }

    public void Warn(string message)
    {
    }

    public void Error(string message)
    {
    }

    public void Stage(string message)
    {
    }

    public void Notify(string title, string message, string? filePath = null)
    {
    }
}
