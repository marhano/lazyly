using PublishTool.Core;

namespace PublishTool.Cli;

internal sealed class ConsoleOutputSink : IOutputSink
{
    public void Info(string message) => Console.WriteLine(message);

    public void Stage(string message) => Console.WriteLine(message);

    public void Notify(string title, string message, string? filePath = null) =>
        Console.WriteLine(filePath is null ? $"{title}: {message}" : $"{title}: {message} ({filePath})");

    public void Warn(string message)
    {
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(message);
        Console.ForegroundColor = originalColor;
    }

    public void Error(string message)
    {
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine(message);
        Console.ForegroundColor = originalColor;
    }
}
