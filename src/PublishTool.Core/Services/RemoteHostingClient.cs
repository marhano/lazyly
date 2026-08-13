using System.Net.Http.Json;
using System.Text.Json;
using PublishTool.Core.Models;

namespace PublishTool.Core.Services;

/// <summary>
/// Talks to a PublishTool.Hosting instance's <c>/api/builds</c> surface over HTTP -- the
/// counterpart to a dev server devs don't have filesystem access to, so this is the only way to
/// get a build there (or list/update/delete/download one) instead of writing straight to a shared
/// BuildsRoot folder the way local/shared-path publishing does.
/// </summary>
public sealed class RemoteHostingClient
{
    /// <summary>Header both this client and the server's API-key check agree on, so the literal
    /// string only exists in one place.</summary>
    public const string ApiKeyHeaderName = "X-PublishTool-Api-Key";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Uploads a build's zip + manifest + optional release notes. Overwrites in place on
    /// the server if the same project+version already exists there (see
    /// <see cref="BuildRepository.ResolvePaths"/>). Throws with the server's own error message on
    /// any non-success response or network failure -- callers that opted into this should have the
    /// failure surfaced loudly, same as a build/deploy failure would be.</summary>
    public async Task UploadBuildAsync(
        string baseUrl, string? apiKey, string zipPath, string manifestPath, string? releaseNotesPath, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        using var zipStream = File.OpenRead(zipPath);
        using var manifestStream = File.OpenRead(manifestPath);
        using var releaseNotesStream = releaseNotesPath is not null && File.Exists(releaseNotesPath)
            ? File.OpenRead(releaseNotesPath)
            : null;

        content.Add(new StreamContent(zipStream), "BuildZip", Path.GetFileName(zipPath));
        content.Add(new StreamContent(manifestStream), "Manifest", Path.GetFileName(manifestPath));
        if (releaseNotesStream is not null)
        {
            content.Add(new StreamContent(releaseNotesStream), "ReleaseNotes", Path.GetFileName(releaseNotesPath!));
        }

        using var request = CreateRequest(HttpMethod.Post, baseUrl, "/api/builds/upload", apiKey);
        request.Content = content;

        using var response = await Http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "upload build", ct);
    }

    public async Task<IReadOnlyList<BuildSummaryDto>> ListBuildsAsync(
        string baseUrl, string? apiKey, string? projectName = null, CancellationToken ct = default)
    {
        var query = projectName is null ? string.Empty : $"?project={Uri.EscapeDataString(projectName)}";
        using var request = CreateRequest(HttpMethod.Get, baseUrl, $"/api/builds{query}", apiKey);
        using var response = await Http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "list builds", ct);

        return await response.Content.ReadFromJsonAsync<List<BuildSummaryDto>>(JsonOptions, ct) ?? new List<BuildSummaryDto>();
    }

    /// <summary>Downloads a build's zip or release notes (identified by the relative path a list
    /// call returned) straight to <paramref name="destinationPath"/>.</summary>
    public async Task DownloadAsync(string baseUrl, string? apiKey, string relativePath, string destinationPath, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, baseUrl, $"/api/builds/download?path={Uri.EscapeDataString(relativePath)}", apiKey);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, "download build", ct);

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var destination = File.Create(destinationPath);
        await source.CopyToAsync(destination, ct);
    }

    /// <summary>Flips <see cref="UpdateBuildRequest.ListInHosting"/> and/or
    /// <see cref="UpdateBuildRequest.IsLatest"/> on an existing build without re-uploading it.</summary>
    public async Task<BuildSummaryDto> UpdateAsync(
        string baseUrl, string? apiKey, string relativePath, bool? listInHosting, bool? isLatest, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Patch, baseUrl, $"/api/builds?path={Uri.EscapeDataString(relativePath)}", apiKey);
        request.Content = JsonContent.Create(new UpdateBuildRequest { ListInHosting = listInHosting, IsLatest = isLatest }, options: JsonOptions);

        using var response = await Http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "update build", ct);

        return await response.Content.ReadFromJsonAsync<BuildSummaryDto>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Server accepted the update but returned no build.");
    }

    public async Task DeleteAsync(string baseUrl, string? apiKey, string relativePath, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Delete, baseUrl, $"/api/builds?path={Uri.EscapeDataString(relativePath)}", apiKey);
        using var response = await Http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "delete build", ct);
    }

    /// <summary>For the Settings tab's "Test connection" button -- never throws, just reports
    /// whether the URL is reachable and the API key is accepted.</summary>
    public async Task<bool> PingAsync(string baseUrl, string? apiKey, CancellationToken ct = default)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, baseUrl, "/api/ping", apiKey);
            using var response = await Http.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string baseUrl, string pathAndQuery, string? apiKey)
    {
        var uri = new Uri(baseUrl.TrimEnd('/') + pathAndQuery, UriKind.Absolute);
        var request = new HttpRequestMessage(method, uri);
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Add(ApiKeyHeaderName, apiKey);
        }

        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string action, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        var detail = string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body;
        throw new InvalidOperationException($"Couldn't {action} ({(int)response.StatusCode}): {detail}");
    }
}
