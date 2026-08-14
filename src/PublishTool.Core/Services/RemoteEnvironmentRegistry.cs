using PublishTool.Core.Models;

namespace PublishTool.Core.Services;

public sealed class RemoteEnvironmentRegistry : IEnvironmentRegistry
{
    private readonly string _baseUrl;
    private readonly string? _apiKey;
    private readonly RemoteHostingClient _client = new();

    public RemoteEnvironmentRegistry(string baseUrl, string? apiKey)
    {
        _baseUrl = baseUrl;
        _apiKey = apiKey;
    }

    public Task<EnvironmentSettings> GetAsync(CancellationToken ct = default) =>
        _client.GetEnvironmentsAsync(_baseUrl, _apiKey, ct);

    public Task SaveAsync(EnvironmentSettings settings, CancellationToken ct = default) =>
        _client.SaveEnvironmentsAsync(_baseUrl, _apiKey, settings, ct);
}
