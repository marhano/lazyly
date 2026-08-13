using PublishTool.Core;

namespace PublishTool.Hosting;

/// <summary>Adapts Core services that log via <see cref="IOutputSink"/> (built for the CLI/GUI) to
/// ASP.NET Core's own logging, so their output ends up in Hosting's normal log stream (console/IIS
/// stdout log) instead of nowhere. Used when Hosting itself calls a Core service like
/// <see cref="Services.BuildDeployer"/> directly, e.g. from the deploy endpoint.</summary>
internal sealed class LoggerOutputSink : IOutputSink
{
    private readonly ILogger _logger;

    public LoggerOutputSink(ILogger logger)
    {
        _logger = logger;
    }

    public void Info(string message) => _logger.LogInformation("{Message}", message);

    public void Stage(string message) => _logger.LogInformation("{Message}", message);

    public void Warn(string message) => _logger.LogWarning("{Message}", message);

    public void Error(string message) => _logger.LogError("{Message}", message);

    public void Notify(string title, string message, string? filePath = null) =>
        _logger.LogInformation("{Title}: {Message}", title, message);
}
