using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SqlFlow.Core;

namespace SqlFlow.Cli.Remote;

/// <summary>One parsed server-sent event: the event name and its (possibly multi-line) JSON data.</summary>
internal readonly record struct SseEvent(string Name, string Data);

/// <summary>The lifecycle answer a cancel endpoint gives.</summary>
internal enum RemoteCancelOutcome
{
    /// <summary>The target was still queued and is now dequeued outright (HTTP 200).</summary>
    Cancelled,

    /// <summary>The target is executing; cancellation was requested for its node to observe (HTTP 202).</summary>
    Cancelling,

    /// <summary>No such run or group (HTTP 404).</summary>
    NotFound,

    /// <summary>The target already finished; there is nothing to cancel (HTTP 409).</summary>
    AlreadyFinished,
}

/// <summary>The two shapes <c>POST /api/v1/runs</c> answers with: a single accepted run, or an accepted group.</summary>
internal sealed record TriggerOutcome(RunTriggerAccepted? Run, RunGroupAccepted? Group);

/// <summary>
/// The typed HTTP client for the control plane's <c>/api/v1</c> surface: the same API the GUI uses, so a CLI
/// verb exercises exactly the code path the browser does. Authentication is a bearer credential (an HS256
/// session JWT or a <c>sqlf_</c> personal access token; the server routes by shape), applied per request from
/// <see cref="BearerToken"/>. Errors are the server's RFC 7807 ProblemDetails, decoded into a
/// <see cref="SqlFlowException"/> whose message carries the status, title, detail, and (for 500s) the
/// correlation id, so one log line is enough to chase the failure server-side. The server redacts secrets from
/// every detail it emits, so the message is safe to print as-is.
/// </summary>
internal sealed class ControlPlaneClient : IDisposable
{
    /// <summary>The web-defaults JSON contract every /api/v1 endpoint speaks (camelCase, case-insensitive).</summary>
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Indented variant for the CLI's --json output.</summary>
    internal static readonly JsonSerializerOptions JsonIndented = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Per-request budget for everything that is not an SSE stream. The HttpClient itself has an
    /// infinite timeout so a live stream can run for as long as the run does.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(100);

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    /// <summary>Constructs a client over an owned <see cref="HttpClient"/> aimed at <paramref name="baseUrl"/>.</summary>
    public ControlPlaneClient(Uri baseUrl)
        : this(new HttpClient { BaseAddress = baseUrl, Timeout = Timeout.InfiniteTimeSpan }, ownsClient: true)
    {
    }

    /// <summary>Constructs a client over an externally supplied <see cref="HttpClient"/> (the in-process test
    /// host's client). The caller keeps ownership unless <paramref name="ownsClient"/> says otherwise; the
    /// client's timeout is raised to infinite so SSE streams are not cut off mid-run.</summary>
    public ControlPlaneClient(HttpClient http, bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _ownsClient = ownsClient;
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    /// <summary>The bearer credential sent with every request; null issues anonymous requests (health, sign-in).</summary>
    public string? BearerToken { get; set; }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }

    // ---- health -------------------------------------------------------------------------------------------

    /// <summary>Probes one of the anonymous health endpoints (<c>/health/live</c> or <c>/health/ready</c>),
    /// returning the status code and the (short) body the health middleware writes.</summary>
    public async Task<(HttpStatusCode Status, string Body)> ProbeHealthAsync(string path, CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Get, path);
        using var timeout = Budget(ct);
        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return (response.StatusCode, body.Trim());
    }

    // ---- authentication -----------------------------------------------------------------------------------

    /// <summary>Signs in a local user, returning the session (a short-lived JWT plus identity facts).</summary>
    public Task<SessionResponse> LoginAsync(string username, string password, CancellationToken ct)
        => PostAsync<SessionResponse>("/api/v1/auth/login", new LoginRequest(username, password), ct);

    /// <summary>Starts the RFC 8628 device grant for the given space-delimited scopes.</summary>
    public Task<DeviceAuthorizationResponse> StartDeviceAuthorizationAsync(string scope, CancellationToken ct)
        => PostAsync<DeviceAuthorizationResponse>("/api/v1/auth/device", new DeviceAuthorizationRequest("sqlflow-cli", scope), ct);

    /// <summary>One poll of the device-token endpoint. Returns the token when approved, or the RFC 8628 error
    /// code (<c>authorization_pending</c>, <c>slow_down</c>, <c>access_denied</c>, <c>expired_token</c>) while
    /// pending or refused; both ride on HTTP 400 by the spec, so this decodes rather than throws.</summary>
    public async Task<(DeviceTokenResponse? Token, string? Error)> PollDeviceTokenAsync(string deviceCode, CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Post, "/api/v1/auth/device/token");
        request.Content = JsonContent(new DeviceTokenRequest(deviceCode));
        using var timeout = Budget(ct);
        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return (Deserialize<DeviceTokenResponse>(body, "/api/v1/auth/device/token"), null);
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            try
            {
                var error = JsonSerializer.Deserialize<DeviceErrorResponse>(body, Json);
                if (error is { Error.Length: > 0 })
                {
                    return (null, error.Error);
                }
            }
            catch (JsonException)
            {
                // Not the RFC error envelope; fall through to the ProblemDetails path.
            }
        }

        throw ToException(response.StatusCode, body);
    }

    /// <summary>Mints a personal access token for the signed-in user (requires a session credential, not a
    /// bootstrap token). The secret in the response is shown exactly once.</summary>
    public Task<CreatedAccessTokenDto> CreateAccessTokenAsync(string name, IReadOnlyList<string>? scopes, int? expiresInDays, CancellationToken ct)
        => PostAsync<CreatedAccessTokenDto>("/api/v1/me/tokens", new CreateAccessTokenRequest(name, scopes, expiresInDays), ct);

    /// <summary>Lists the caller's own personal access tokens (also the cheapest authenticated probe: a 401
    /// here means the credential is bad).</summary>
    public Task<IReadOnlyList<AccessTokenDto>> ListAccessTokensAsync(CancellationToken ct)
        => GetAsync<IReadOnlyList<AccessTokenDto>>("/api/v1/me/tokens", ct);

    /// <summary>Revokes one of the caller's tokens. False when the server no longer knows it (already revoked,
    /// expired away, or not owned), which callers treat as "nothing left to do".</summary>
    public async Task<bool> RevokeAccessTokenAsync(Guid tokenId, CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Delete, $"/api/v1/me/tokens/{tokenId}");
        using var timeout = Budget(ct);
        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, timeout.Token).ConfigureAwait(false);
        return true;
    }

    // ---- repos --------------------------------------------------------------------------------------------

    /// <summary>Every repo the catalog knows, walking the pages so name resolution sees the whole estate.</summary>
    public async Task<IReadOnlyList<RepoDto>> ListReposAsync(CancellationToken ct)
    {
        var repos = new List<RepoDto>();
        for (var page = 1; ; page++)
        {
            var result = await GetAsync<PagedResult<RepoDto>>($"/api/v1/repos?page={page}&pageSize=200", ct).ConfigureAwait(false);
            repos.AddRange(result.Items);
            if (result.Items.Count == 0 || repos.Count >= result.Total)
            {
                return repos;
            }
        }
    }

    // ---- runs ---------------------------------------------------------------------------------------------

    /// <summary>Previews what a node/batch scope would enqueue, without enqueuing anything.</summary>
    public Task<RunScopePreviewDto> PreviewScopeAsync(Guid repoId, string? flowName, string scope, string? batch, bool includeAll, CancellationToken ct)
    {
        var query = new QueryBuilder()
            .Add("repoId", repoId.ToString())
            .Add("flowName", flowName)
            .Add("scope", scope)
            .Add("batch", batch)
            .Add("includeAll", includeAll ? "true" : null);
        return GetAsync<RunScopePreviewDto>($"/api/v1/runs/preview{query}", ct);
    }

    /// <summary>Triggers a run. The server answers with one of two accepted shapes; both are decoded here and
    /// exactly one side of the returned <see cref="TriggerOutcome"/> is non-null.</summary>
    public async Task<TriggerOutcome> TriggerRunAsync(RunTriggerRequest request, CancellationToken ct)
    {
        using var message = NewRequest(HttpMethod.Post, "/api/v1/runs");
        message.Content = JsonContent(request);
        using var timeout = Budget(ct);
        using var response = await _http.SendAsync(message, timeout.Token).ConfigureAwait(false);
        await EnsureSuccessAsync(response, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

        using var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("groupId", out _))
        {
            return new TriggerOutcome(null, Deserialize<RunGroupAccepted>(body, "/api/v1/runs"));
        }

        return new TriggerOutcome(Deserialize<RunTriggerAccepted>(body, "/api/v1/runs"), null);
    }

    /// <summary>The runs list with the same filters the GUI's run inbox uses.</summary>
    public Task<PagedResult<RunSummaryDto>> ListRunsAsync(
        Guid? repoId, string? status, string? flowName, string? batch, string? flowKind, Guid? groupId,
        bool latest, int page, int pageSize, CancellationToken ct)
    {
        var query = new QueryBuilder()
            .Add("repoId", repoId?.ToString())
            .Add("status", status)
            .Add("flowName", flowName)
            .Add("batch", batch)
            .Add("flowKind", flowKind)
            .Add("groupId", groupId?.ToString())
            .Add("latest", latest ? "true" : null)
            .Add("page", page.ToString(CultureInfo.InvariantCulture))
            .Add("pageSize", pageSize.ToString(CultureInfo.InvariantCulture));
        return GetAsync<PagedResult<RunSummaryDto>>($"/api/v1/runs{query}", ct);
    }

    /// <summary>One run's full header; null when the server does not know the id.</summary>
    public Task<RunDetailDto?> GetRunAsync(Guid runId, CancellationToken ct)
        => GetOrNullAsync<RunDetailDto>($"/api/v1/runs/{runId}", ct);

    /// <summary>A paged drill-down section under a run (files, statements, assertions, keys, metrics).</summary>
    public Task<PagedResult<T>> GetRunSectionAsync<T>(Guid runId, string section, int page, int pageSize, CancellationToken ct)
        => GetAsync<PagedResult<T>>(
            $"/api/v1/runs/{runId}/{section}?page={page.ToString(CultureInfo.InvariantCulture)}&pageSize={pageSize.ToString(CultureInfo.InvariantCulture)}", ct);

    /// <summary>The whole consolidated trace rendered server-side as one plain-text document.</summary>
    public async Task<string> GetRunTraceTextAsync(Guid runId, CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Get, $"/api/v1/runs/{runId}/trace/text");
        using var timeout = Budget(ct);
        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        await EnsureSuccessAsync(response, timeout.Token).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
    }

    /// <summary>Cancels a run through the control plane, honoring its lifecycle.</summary>
    public Task<RemoteCancelOutcome> CancelRunAsync(Guid runId, CancellationToken ct)
        => PostCancelAsync($"/api/v1/runs/{runId}/cancel", ct);

    // ---- run groups ---------------------------------------------------------------------------------------

    /// <summary>One run group's header and live member rollup; null when the server does not know the id.</summary>
    public Task<RunGroupDto?> GetRunGroupAsync(Guid groupId, CancellationToken ct)
        => GetOrNullAsync<RunGroupDto>($"/api/v1/runs/groups/{groupId}", ct);

    /// <summary>Cancels a whole run group.</summary>
    public Task<RemoteCancelOutcome> CancelGroupAsync(Guid groupId, CancellationToken ct)
        => PostCancelAsync($"/api/v1/runs/groups/{groupId}/cancel", ct);

    // ---- server-sent events -------------------------------------------------------------------------------

    /// <summary>
    /// Consumes one SSE endpoint (<c>event: name\ndata: json\n\n</c> frames), yielding each event as it
    /// arrives. Heartbeat comment lines (<c>: hb</c>) keep the connection alive and are swallowed here. The
    /// stream ends when the server closes it (after its terminal <c>end</c> event) or the token cancels;
    /// a transport drop surfaces as the underlying <see cref="IOException"/>/<see cref="HttpRequestException"/>
    /// for the caller's reconnect logic.
    /// </summary>
    public async IAsyncEnumerable<SseEvent> StreamAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? eventName = null;
            var data = new StringBuilder();
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    if (eventName is not null || data.Length > 0)
                    {
                        yield return new SseEvent(eventName ?? "message", data.ToString());
                        eventName = null;
                        data.Clear();
                    }

                    continue;
                }

                if (line[0] == ':')
                {
                    continue; // heartbeat / comment
                }

                if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    eventName = line["event:".Length..].Trim();
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0)
                    {
                        data.Append('\n');
                    }

                    data.Append(line["data:".Length..].TrimStart());
                }
            }
        }
    }

    /// <summary>Deserializes one SSE event's data with the API's JSON contract.</summary>
    internal static T ParseEvent<T>(SseEvent sse)
        => Deserialize<T>(sse.Data, $"SSE '{sse.Name}' event");

    // ---- identity and summary -----------------------------------------------------------------------------

    /// <summary>Who the presented credential authenticates as (the cheapest authoritative probe).</summary>
    public Task<IdentityDto> GetIdentityAsync(CancellationToken ct)
        => GetAsync<IdentityDto>("/api/v1/me", ct);

    /// <summary>The dashboard rollup: estate size, run queue, fleet, scheduling, managed sync.</summary>
    public Task<DashboardDto> GetSummaryAsync(CancellationToken ct)
        => GetAsync<DashboardDto>("/api/v1/summary", ct);

    /// <summary>The worker fleet, most recently seen first.</summary>
    public Task<PagedResult<NodeDto>> ListNodesAsync(int page, int pageSize, CancellationToken ct)
        => GetAsync<PagedResult<NodeDto>>($"/api/v1/nodes{Paging(page, pageSize)}", ct);

    // ---- schedules ----------------------------------------------------------------------------------------

    public Task<PagedResult<ScheduleDto>> ListSchedulesAsync(
        Guid? repoId, Guid? pipelineId, bool? enabled, int page, int pageSize, CancellationToken ct)
    {
        var query = new QueryBuilder()
            .Add("repoId", repoId?.ToString())
            .Add("pipelineId", pipelineId?.ToString())
            .Add("enabled", enabled?.ToString().ToLowerInvariant())
            .Add("page", page.ToString(CultureInfo.InvariantCulture))
            .Add("pageSize", pageSize.ToString(CultureInfo.InvariantCulture));
        return GetAsync<PagedResult<ScheduleDto>>($"/api/v1/schedules{query}", ct);
    }

    public Task<ScheduleDto?> GetScheduleAsync(Guid id, CancellationToken ct)
        => GetOrNullAsync<ScheduleDto>($"/api/v1/schedules/{id}", ct);

    public Task<ScheduleCreated> CreateScheduleAsync(CreateScheduleRequest request, CancellationToken ct)
        => PostAsync<ScheduleCreated>("/api/v1/schedules", request, ct);

    /// <summary>Fires a schedule now, enqueuing a run of its flow (to test the schedule). Returns the enqueued run
    /// id, or null when the server does not know the schedule id (404). A 409 (the flow is inactive/removed) surfaces
    /// as a failure carrying the server's detail.</summary>
    public async Task<ScheduleRunAccepted?> RunScheduleNowAsync(Guid id, CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Post, $"/api/v1/schedules/{id}/run");
        using var timeout = Budget(ct);
        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, timeout.Token).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return Deserialize<ScheduleRunAccepted>(text, $"/api/v1/schedules/{id}/run");
    }

    public Task<ScheduleDto?> PauseScheduleAsync(Guid id, CancellationToken ct)
        => PostOrNullAsync<ScheduleDto>($"/api/v1/schedules/{id}/pause", null, ct);

    public Task<ScheduleDto?> ResumeScheduleAsync(Guid id, CancellationToken ct)
        => PostOrNullAsync<ScheduleDto>($"/api/v1/schedules/{id}/resume", null, ct);

    /// <summary>Deletes a schedule. False when the server does not know the id.</summary>
    public async Task<bool> DeleteScheduleAsync(Guid id, CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Delete, $"/api/v1/schedules/{id}");
        using var timeout = Budget(ct);
        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, timeout.Token).ConfigureAwait(false);
        return true;
    }

    // ---- repo sources -------------------------------------------------------------------------------------

    public Task<PagedResult<RepoSourceDto>> ListRepoSourcesAsync(int page, int pageSize, CancellationToken ct)
        => GetAsync<PagedResult<RepoSourceDto>>($"/api/v1/repos/sources{Paging(page, pageSize)}", ct);

    public Task<RepoSourceRegistered> RegisterRepoSourceAsync(RegisterRepoSourceRequest request, CancellationToken ct)
        => PostAsync<RepoSourceRegistered>("/api/v1/repos/sources", request, ct);

    public Task<IReadOnlyList<DiscoveredFlowDto>> DiscoverRepoAsync(DiscoverRepoRequest request, CancellationToken ct)
        => PostAsync<IReadOnlyList<DiscoveredFlowDto>>("/api/v1/repos/discover", request, ct);

    /// <summary>Forces a managed source's sync now; null when no enabled source has the id.</summary>
    public Task<RepoSourceDto?> SyncRepoSourceAsync(Guid sourceId, CancellationToken ct)
        => PostOrNullAsync<RepoSourceDto>($"/api/v1/repos/sources/{sourceId}/sync", null, ct);

    /// <summary>Re-syncs a local-path repo (the CLI-registered kind) through the control plane.</summary>
    public Task<RepoSyncResultDto?> SyncLocalRepoAsync(Guid repoId, CancellationToken ct)
        => PostOrNullAsync<RepoSyncResultDto>($"/api/v1/repos/{repoId}/sync", null, ct);

    // ---- pipelines ----------------------------------------------------------------------------------------

    public Task<PagedResult<PipelineSummaryDto>> ListPipelinesAsync(
        Guid? repoId, string? kind, bool? active, string? name, int page, int pageSize, CancellationToken ct)
    {
        var query = new QueryBuilder()
            .Add("repoId", repoId?.ToString())
            .Add("kind", kind)
            .Add("active", active?.ToString().ToLowerInvariant())
            .Add("name", name)
            .Add("page", page.ToString(CultureInfo.InvariantCulture))
            .Add("pageSize", pageSize.ToString(CultureInfo.InvariantCulture));
        return GetAsync<PagedResult<PipelineSummaryDto>>($"/api/v1/pipelines{query}", ct);
    }

    public Task<PipelineDetailDto?> GetPipelineAsync(Guid id, CancellationToken ct)
        => GetOrNullAsync<PipelineDetailDto>($"/api/v1/pipelines/{id}", ct);

    public Task<IReadOnlyList<PipelineColumnDto>> GetPipelineColumnsAsync(Guid id, string? kind, CancellationToken ct)
        => GetAsync<IReadOnlyList<PipelineColumnDto>>(
            $"/api/v1/pipelines/{id}/columns{new QueryBuilder().Add("kind", kind)}", ct);

    public Task<PagedResult<PipelineFileDto>> GetPipelineFilesAsync(
        Guid id, string? search, int page, int pageSize, CancellationToken ct)
    {
        var query = new QueryBuilder()
            .Add("search", search)
            .Add("page", page.ToString(CultureInfo.InvariantCulture))
            .Add("pageSize", pageSize.ToString(CultureInfo.InvariantCulture));
        return GetAsync<PagedResult<PipelineFileDto>>($"/api/v1/pipelines/{id}/files{query}", ct);
    }

    // ---- datasources and compute tasks --------------------------------------------------------------------

    public Task<IReadOnlyList<DatasourceDto>> ListDatasourcesAsync(CancellationToken ct)
        => GetAsync<IReadOnlyList<DatasourceDto>>("/api/v1/datasources", ct);

    public Task<ComputeTaskAccepted> CreateComputeTaskAsync(ComputeTaskRequest request, CancellationToken ct)
        => PostAsync<ComputeTaskAccepted>("/api/v1/datasources/tasks", request, ct);

    /// <summary>One compute task, optionally long-polling (<paramref name="waitMs"/> capped server-side at
    /// 20s); null when the server does not know the id.</summary>
    public Task<ComputeTaskDto?> GetComputeTaskAsync(Guid taskId, int waitMs, CancellationToken ct)
        => GetOrNullAsync<ComputeTaskDto>(
            $"/api/v1/datasources/tasks/{taskId}?waitMs={waitMs.ToString(CultureInfo.InvariantCulture)}", ct);

    public Task<PagedResult<ComputeTaskSummaryDto>> ListComputeTasksAsync(
        string? status, string? reference, string? operation, int page, int pageSize, CancellationToken ct)
    {
        var query = new QueryBuilder()
            .Add("status", status)
            .Add("reference", reference)
            .Add("operation", operation)
            .Add("page", page.ToString(CultureInfo.InvariantCulture))
            .Add("pageSize", pageSize.ToString(CultureInfo.InvariantCulture));
        return GetAsync<PagedResult<ComputeTaskSummaryDto>>($"/api/v1/datasources/tasks{query}", ct);
    }

    public Task<RemoteCancelOutcome> CancelComputeTaskAsync(Guid taskId, CancellationToken ct)
        => PostCancelAsync($"/api/v1/datasources/tasks/{taskId}/cancel", ct);

    // ---- search -------------------------------------------------------------------------------------------

    /// <summary>The combined search: every category counted in full, top hits previewed.</summary>
    public Task<AllSearchDto> SearchAllAsync(string term, CancellationToken ct)
        => GetAsync<AllSearchDto>($"/api/v1/search/all{new QueryBuilder().Add("q", term)}", ct);

    /// <summary>One search category. <paramref name="paramName"/> is the endpoint's term parameter: the
    /// objects/columns/files routes take <c>name</c>, definitions/flows take <c>q</c>.</summary>
    public Task<PagedResult<T>> SearchAsync<T>(
        string category, string paramName, string term, int page, int pageSize, CancellationToken ct)
    {
        var query = new QueryBuilder()
            .Add(paramName, term)
            .Add("page", page.ToString(CultureInfo.InvariantCulture))
            .Add("pageSize", pageSize.ToString(CultureInfo.InvariantCulture));
        return GetAsync<PagedResult<T>>($"/api/v1/search/{category}{query}", ct);
    }

    // ---- lineage ------------------------------------------------------------------------------------------

    public Task<PagedResult<ObjectDto>> ListLineageObjectsAsync(
        string? name, string? serverRef, string? database, string? schema, string? kind,
        int page, int pageSize, CancellationToken ct)
    {
        var query = new QueryBuilder()
            .Add("name", name)
            .Add("serverRef", serverRef)
            .Add("database", database)
            .Add("schema", schema)
            .Add("kind", kind)
            .Add("page", page.ToString(CultureInfo.InvariantCulture))
            .Add("pageSize", pageSize.ToString(CultureInfo.InvariantCulture));
        return GetAsync<PagedResult<ObjectDto>>($"/api/v1/lineage/objects{query}", ct);
    }

    public Task<PagedResult<EdgeDto>> ListLineageEdgesAsync(
        Guid repoId, Guid? pipelineId, string? objectKey, string? relation, string? tier,
        int page, int pageSize, CancellationToken ct)
    {
        var query = new QueryBuilder()
            .Add("pipelineId", pipelineId?.ToString())
            .Add("objectKey", objectKey)
            .Add("relation", relation)
            .Add("tier", tier)
            .Add("page", page.ToString(CultureInfo.InvariantCulture))
            .Add("pageSize", pageSize.ToString(CultureInfo.InvariantCulture));
        return GetAsync<PagedResult<EdgeDto>>($"/api/v1/repos/{repoId}/lineage/edges{query}", ct);
    }

    /// <summary>A repo's execution plan: the dependency waves lineage computed.</summary>
    public Task<IReadOnlyList<WaveDto>> GetWavesAsync(Guid repoId, CancellationToken ct)
        => GetAsync<IReadOnlyList<WaveDto>>($"/api/v1/repos/{repoId}/waves", ct);

    /// <summary>The code behind any lineage node (an object key, or a pipeline name/id); null when unknown.</summary>
    public Task<NodeScriptDto?> GetNodeScriptAsync(string key, CancellationToken ct)
        => GetOrNullAsync<NodeScriptDto>($"/api/v1/lineage/script{new QueryBuilder().Add("key", key)}", ct);

    // ---- plumbing -----------------------------------------------------------------------------------------

    private static string Paging(int page, int pageSize)
        => $"?page={page.ToString(CultureInfo.InvariantCulture)}&pageSize={pageSize.ToString(CultureInfo.InvariantCulture)}";

    private async Task<T?> PostOrNullAsync<T>(string path, object? body, CancellationToken ct)
        where T : class
    {
        using var request = NewRequest(HttpMethod.Post, path);
        if (body is not null)
        {
            request.Content = JsonContent(body);
        }

        using var timeout = Budget(ct);
        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, timeout.Token).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return Deserialize<T>(text, path);
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Get, path);
        using var timeout = Budget(ct);
        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        await EnsureSuccessAsync(response, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return Deserialize<T>(body, path);
    }

    private async Task<T?> GetOrNullAsync<T>(string path, CancellationToken ct)
        where T : class
    {
        using var request = NewRequest(HttpMethod.Get, path);
        using var timeout = Budget(ct);
        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return Deserialize<T>(body, path);
    }

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Post, path);
        request.Content = JsonContent(body);
        using var timeout = Budget(ct);
        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        await EnsureSuccessAsync(response, timeout.Token).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return Deserialize<T>(text, path);
    }

    private async Task<RemoteCancelOutcome> PostCancelAsync(string path, CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Post, path);
        using var timeout = Budget(ct);
        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
                return RemoteCancelOutcome.Cancelled;
            case HttpStatusCode.Accepted:
                return RemoteCancelOutcome.Cancelling;
            case HttpStatusCode.NotFound:
                return RemoteCancelOutcome.NotFound;
            case HttpStatusCode.Conflict:
                return RemoteCancelOutcome.AlreadyFinished;
            default:
                await EnsureSuccessAsync(response, timeout.Token).ConfigureAwait(false);
                // A success status outside the contract's set (the server never sends one today): report it
                // as the weaker of the two acknowledgements rather than inventing a new state.
                return RemoteCancelOutcome.Cancelling;
        }
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        if (BearerToken is { Length: > 0 } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private static StringContent JsonContent(object body)
        => new(JsonSerializer.Serialize(body, body.GetType(), Json), Encoding.UTF8, "application/json");

    private static CancellationTokenSource Budget(CancellationToken ct)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        source.CancelAfter(RequestTimeout);
        return source;
    }

    private static T Deserialize<T>(string body, string context)
    {
        try
        {
            var value = JsonSerializer.Deserialize<T>(body, Json);
            return value is null
                ? throw new SqlFlowException($"The control plane's {context} response was empty JSON.")
                : value;
        }
        catch (JsonException ex)
        {
            throw new SqlFlowException($"The control plane's {context} response was not the expected JSON: {ex.Message}");
        }
    }

    /// <summary>Turns a non-success response into a <see cref="SqlFlowException"/> carrying the ProblemDetails
    /// title/detail (already secret-redacted server-side) and the correlation id when one is present.</summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw ToException(response.StatusCode, body);
    }

    private static SqlFlowException ToException(HttpStatusCode status, string body)
    {
        string? title = null;
        string? detail = null;
        string? correlationId = null;
        if (body.Length > 0)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                    detail = root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                    correlationId = root.TryGetProperty("correlationId", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                }
            }
            catch (JsonException)
            {
                // Not ProblemDetails (a proxy page, an empty 502): fall back to the raw body, trimmed hard so
                // an HTML error page does not flood the console.
                detail = body.Length > 300 ? body[..300] : body;
            }
        }

        var message = new StringBuilder()
            .Append("the control plane answered ")
            .Append((int)status)
            .Append(' ')
            .Append(status);
        if (!string.IsNullOrWhiteSpace(title))
        {
            message.Append(": ").Append(title);
        }

        if (!string.IsNullOrWhiteSpace(detail))
        {
            message.Append(": ").Append(detail.Trim());
        }

        if (status == HttpStatusCode.Unauthorized)
        {
            message.Append(" (the bearer credential was rejected; sign in with 'sqlflow login', or set --token / SQLFLOW_TOKEN)");
        }
        else if (status == HttpStatusCode.Forbidden)
        {
            message.Append(" (the credential lacks the required scope; only user administration is restricted, and it needs 'admin')");
        }

        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            message.Append(" [correlation ").Append(correlationId).Append(']');
        }

        return new SqlFlowException(message.ToString());
    }

    /// <summary>Minimal query-string assembly: skips null/blank values, escapes everything else.</summary>
    private sealed class QueryBuilder
    {
        private readonly StringBuilder _query = new();

        public QueryBuilder Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                _query.Append(_query.Length == 0 ? '?' : '&').Append(name).Append('=').Append(Uri.EscapeDataString(value));
            }

            return this;
        }

        public override string ToString() => _query.ToString();
    }
}
