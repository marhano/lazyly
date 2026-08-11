namespace PublishTool.Core.Models;

public sealed class DependencyCheckResult
{
    public required string Name { get; set; }

    public required bool IsAvailable { get; set; }

    /// <summary>The resolved path when available, or the reason it isn't, when not.</summary>
    public required string Details { get; set; }

    public string StatusText => IsAvailable ? "✓ OK" : "✗ Missing";
}
