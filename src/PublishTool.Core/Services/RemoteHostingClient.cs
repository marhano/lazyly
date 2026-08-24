using System.Net.Http.Json;
using System.Text.Json;
using PublishTool.Core.Models;

namespace PublishTool.Core.Services;

/// <summary>Thrown when the dev server 404s an endpoint this client knows about -- meaning the
/// server is running a version of PublishTool.Hosting that predates it, not that anything is
/// actually wrong. Callers should show a specific "needs an updated dev server" message instead of
/// a generic error for this.</summary>
public sealed class RemoteFeatureNotAvailableException : InvalidOperationException
{
    public RemoteFeatureNotAvailableException(string message) : base(message)
    {
    }
}

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

    private sealed record UploadResponse(string ProjectName, string Version, string ManifestPath);

    /// <summary>Uploads a build's zip + manifest + optional release notes. Overwrites in place on
    /// the server if the same project+version already exists there (see
    /// <see cref="BuildRepository.ResolvePaths"/>). Throws with the server's own error message on
    /// any non-success response or network failure -- callers that opted into this should have the
    /// failure surfaced loudly, same as a build/deploy failure would be. Returns the build's
    /// manifest path relative to the server's BuildsRoot, for passing straight to
    /// <see cref="DeployAsync"/> or any other path-taking call.</summary>
    public async Task<string> UploadBuildAsync(
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

        var parsed = await response.Content.ReadFromJsonAsync<UploadResponse>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Server accepted the upload but returned no manifest path.");
        return parsed.ManifestPath;
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

    // ---------------------------------------------------------------------------------------
    // Project registry (/api/projects) -- see RemoteProjectRegistry, which is the only caller
    // that should normally use these directly.
    // ---------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<SharedProjectConfig>> GetProjectsAsync(string baseUrl, string? apiKey, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, baseUrl, "/api/projects", apiKey);
        using var response = await Http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "list projects", ct);

        return await response.Content.ReadFromJsonAsync<List<SharedProjectConfig>>(JsonOptions, ct) ?? new List<SharedProjectConfig>();
    }

    public async Task<SharedProjectConfig?> GetProjectAsync(string baseUrl, string? apiKey, string name, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, baseUrl, $"/api/projects/{Uri.EscapeDataString(name)}", apiKey);
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, "get project", ct);
        return await response.Content.ReadFromJsonAsync<SharedProjectConfig>(JsonOptions, ct);
    }

    public async Task UpsertProjectAsync(string baseUrl, string? apiKey, SharedProjectConfig project, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Put, baseUrl, $"/api/projects/{Uri.EscapeDataString(project.Name)}", apiKey);
        request.Content = JsonContent.Create(project, options: JsonOptions);

        using var response = await Http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "save project", ct);
    }

    public async Task DeleteProjectAsync(string baseUrl, string? apiKey, string name, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Delete, baseUrl, $"/api/projects/{Uri.EscapeDataString(name)}", apiKey);
        using var response = await Http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "delete project", ct);
    }

    private sealed record ReserveSequenceResponse(int Sequence);

    /// <summary>Atomically reserves and returns the next release-notes sequence number for a
    /// project -- the server-side counterpart to <see cref="ProjectRegistry.ReserveNextReleaseSequenceAsync"/>,
    /// used by <see cref="RemoteProjectRegistry"/> to avoid the race a plain read-increment-write
    /// would have once the counter is shared across a team.</summary>
    public async Task<int> ReserveReleaseSequenceAsync(string baseUrl, string? apiKey, string projectName, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Post, baseUrl, $"/api/projects/{Uri.EscapeDataString(projectName)}/reserve-release-sequence", apiKey);
        using var response = await Http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "reserve release sequence", ct);

        var parsed = await response.Content.ReadFromJsonAsync<ReserveSequenceResponse>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Server accepted the request but returned no sequence number.");
        return parsed.Sequence;
    }

    // ---------------------------------------------------------------------------------------
    // Deploy (/api/deploy)
    // ---------------------------------------------------------------------------------------

    /// <summary>Extracts an already-uploaded build (identified by the relative manifest path a
    /// list/upload call returned) and deploys it to the dev server's own IIS, using the named entry
    /// from that project's <see cref="SharedProjectConfig.RemoteEnvironments"/>. Called automatically
    /// by <see cref="Publisher"/> when a publish selects a matching environment, or manually from
    /// the Projects tab for a specific (possibly older) version.</summary>
    /// <param name="deployedBy">The calling machine's user -- recorded in the server's own
    /// deployment history (see <see cref="SiteDeploymentStore"/>) instead of the server's own
    /// service identity, since that's the person who actually decided to deploy.</param>
    public async Task DeployAsync(
        string baseUrl, string? apiKey, string manifestRelativePath, string environmentName, string deployedBy, CancellationToken ct = default)
    {
        using var request = CreateRequest(
            HttpMethod.Post, baseUrl,
            $"/api/deploy?path={Uri.EscapeDataString(manifestRelativePath)}&environment={Uri.EscapeDataString(environmentName)}" +
            $"&deployedBy={Uri.EscapeDataString(deployedBy)}", apiKey);
        using var response = await Http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "deploy build", ct);
    }

    // ---------------------------------------------------------------------------------------
    // Deployment environments (/api/environments) -- the shared counterpart to
    // LocalEnvironmentRegistry, used by RemoteEnvironmentRegistry when remote mode is on.
    // ---------------------------------------------------------------------------------------

    public async Task<EnvironmentSettings> GetEnvironmentsAsync(string baseUrl, string? apiKey, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, baseUrl, "/api/environments", apiKey);
        using var response = await Http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "list deployment environments", ct);

        return await response.Content.ReadFromJsonAsync<EnvironmentSettings>(JsonOptions, ct) ?? new EnvironmentSettings();
    }

    public async Task SaveEnvironmentsAsync(string baseUrl, string? apiKey, EnvironmentSettings settings, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Put, baseUrl, "/api/environments", apiKey);
        request.Content = JsonContent.Create(settings, options: JsonOptions);

        using var response = await Http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "save deployment environments", ct);
    }

    // ---------------------------------------------------------------------------------------
    // Remote IIS management (/api/iis/*) -- Hosting manages its own machine's IIS; these just
    // call over HTTP instead of local appcmd.
    // ---------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<IisSiteStatus>> ListRemoteSitesAsync(string baseUrl, string? apiKey, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, baseUrl, "/api/iis/sites", apiKey);
        using var response = await Http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "list remote IIS sites", ct);

        return await response.Content.ReadFromJsonAsync<List<IisSiteStatus>>(JsonOptions, ct) ?? new List<IisSiteStatus>();
    }

    public async Task<IReadOnlyList<IisAppPoolStatus>> ListRemoteAppPoolsAsync(string baseUrl, string? apiKey, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, baseUrl, "/api/iis/apppools", apiKey);
        using var response = await Http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "list remote IIS application pools", ct);

        return await response.Content.ReadFromJsonAsync<List<IisAppPoolStatus>>(JsonOptions, ct) ?? new List<IisAppPoolStatus>();
    }

    public Task StartRemoteSiteAsync(string baseUrl, string? apiKey, string siteName, CancellationToken ct = default) =>
        PostRemoteIisAction(baseUrl, apiKey, $"/api/iis/sites/{Uri.EscapeDataString(siteName)}/start", "start remote IIS site", ct);

    public Task StopRemoteSiteAsync(string baseUrl, string? apiKey, string siteName, CancellationToken ct = default) =>
        PostRemoteIisAction(baseUrl, apiKey, $"/api/iis/sites/{Uri.EscapeDataString(siteName)}/stop", "stop remote IIS site", ct);

    public Task StartRemoteAppPoolAsync(string baseUrl, string? apiKey, string poolName, CancellationToken ct = default) =>
        PostRemoteIisAction(baseUrl, apiKey, $"/api/iis/apppools/{Uri.EscapeDataString(poolName)}/start", "start remote application pool", ct);

    public Task StopRemoteAppPoolAsync(string baseUrl, string? apiKey, string poolName, CancellationToken ct = default) =>
        PostRemoteIisAction(baseUrl, apiKey, $"/api/iis/apppools/{Uri.EscapeDataString(poolName)}/stop", "stop remote application pool", ct);

    public Task RecycleRemoteAppPoolAsync(string baseUrl, string? apiKey, string poolName, CancellationToken ct = default) =>
        PostRemoteIisAction(baseUrl, apiKey, $"/api/iis/apppools/{Uri.EscapeDataString(poolName)}/recycle", "recycle remote application pool", ct);

    /// <summary>Full deployment history (newest-first) for one site on the dev server's own IIS --
    /// for the IIS tab's History dialog in remote mode. Throws a 404 <see cref="InvalidOperationException"/>
    /// against an older Hosting server that predates this endpoint -- callers should show a specific
    /// "needs an updated dev server" message rather than a generic error for that case.</summary>
    public async Task<IReadOnlyList<SiteDeploymentRecord>> GetRemoteSiteDeploymentHistoryAsync(
        string baseUrl, string? apiKey, string siteName, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, baseUrl, $"/api/iis/sites/{Uri.EscapeDataString(siteName)}/history", apiKey);
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new RemoteFeatureNotAvailableException(
                "Deployment history isn't available yet -- this dev server needs PublishTool.Hosting redeployed.");
        }

        await EnsureSuccessAsync(response, "read remote deployment history", ct);

        return await response.Content.ReadFromJsonAsync<List<SiteDeploymentRecord>>(JsonOptions, ct) ?? new List<SiteDeploymentRecord>();
    }

    private async Task PostRemoteIisAction(string baseUrl, string? apiKey, string pathAndQuery, string action, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Post, baseUrl, pathAndQuery, apiKey);
        using var response = await Http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, action, ct);
    }

    // ---------------------------------------------------------------------------------------
    // Firewall rules (/api/firewall/*) -- Hosting manages its own machine's inbound Windows
    // Firewall rules for ports IIS sites use, same local/remote split as /api/iis/*.
    // ---------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<FirewallRuleStatus>> ListRemoteFirewallRulesAsync(
        string baseUrl, string? apiKey, bool includeAllRules = false, CancellationToken ct = default)
    {
        var query = includeAllRules ? "?all=true" : string.Empty;
        using var request = CreateRequest(HttpMethod.Get, baseUrl, $"/api/firewall/rules{query}", apiKey);
        using var response = await Http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "list remote firewall rules", ct);

        return await response.Content.ReadFromJsonAsync<List<FirewallRuleStatus>>(JsonOptions, ct) ?? new List<FirewallRuleStatus>();
    }

    private sealed record AddFirewallRuleRequest(string Label, string Ports, string Protocol, string PerformedBy);

    public async Task AddRemoteFirewallRuleAsync(
        string baseUrl, string? apiKey, string label, string ports, string protocol, string performedBy, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Post, baseUrl, "/api/firewall/rules", apiKey);
        request.Content = JsonContent.Create(new AddFirewallRuleRequest(label, ports, protocol, performedBy), options: JsonOptions);

        using var response = await Http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "add remote firewall rule", ct);
    }

    private sealed record EditFirewallRuleRequest(string CurrentName, string NewLabel, string Ports, string Protocol, string PerformedBy);

    public async Task EditRemoteFirewallRuleAsync(
        string baseUrl, string? apiKey, string currentName, string newLabel, string ports, string protocol, string performedBy,
        CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Put, baseUrl, "/api/firewall/rules", apiKey);
        request.Content = JsonContent.Create(new EditFirewallRuleRequest(currentName, newLabel, ports, protocol, performedBy), options: JsonOptions);

        using var response = await Http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "edit remote firewall rule", ct);
    }

    public async Task DeleteRemoteFirewallRuleAsync(
        string baseUrl, string? apiKey, string ruleName, string performedBy, CancellationToken ct = default)
    {
        using var request = CreateRequest(
            HttpMethod.Delete, baseUrl,
            $"/api/firewall/rules?name={Uri.EscapeDataString(ruleName)}&performedBy={Uri.EscapeDataString(performedBy)}", apiKey);
        using var response = await Http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "delete remote firewall rule", ct);
    }

    /// <summary>Full Add/Edit/Remove audit trail (newest-first) for the dev server's own
    /// firewall rules. Throws a 404 <see cref="RemoteFeatureNotAvailableException"/> against an
    /// older Hosting server that predates this endpoint.</summary>
    public async Task<IReadOnlyList<FirewallAuditEntry>> GetRemoteFirewallAuditAsync(string baseUrl, string? apiKey, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, baseUrl, "/api/firewall/audit", apiKey);
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new RemoteFeatureNotAvailableException(
                "Firewall audit history isn't available yet -- this dev server needs PublishTool.Hosting redeployed.");
        }

        await EnsureSuccessAsync(response, "read remote firewall audit history", ct);

        return await response.Content.ReadFromJsonAsync<List<FirewallAuditEntry>>(JsonOptions, ct) ?? new List<FirewallAuditEntry>();
    }

    // ---------------------------------------------------------------------------------------
    // Event Logs (/api/eventlog) -- Hosting reads its own local Windows Event Log using the
    // project's shared EventLog* settings.
    // ---------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<EventLogEntryRecord>> GetEventLogAsync(string baseUrl, string? apiKey, string projectName, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, baseUrl, $"/api/eventlog?project={Uri.EscapeDataString(projectName)}", apiKey);
        using var response = await Http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "read remote event log", ct);

        return await response.Content.ReadFromJsonAsync<List<EventLogEntryRecord>>(JsonOptions, ct) ?? new List<EventLogEntryRecord>();
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
