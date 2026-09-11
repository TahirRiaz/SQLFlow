using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SqlFlow.Dispatch;
using SqlFlow.Dispatch.Protocol;

namespace SqlFlow.Node;

/// <summary>
/// The node protocol over HTTP: a standalone worker's transport to the control plane's dispatcher. Every call is
/// outbound from the node with a bearer credential carrying the <c>node</c> scope. The poll's request timeout is
/// budgeted from the wait it asks for (the server may hold it that long), outcome reports get a fixed budget, and
/// a 503 (a passive replica, or the owner still rebuilding) is reported as retryable so the drain loop backs off
/// and tries again rather than failing.
/// </summary>
public sealed class HttpNodeTransport : INodeTransport, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Slack added to a poll's requested wait for the network round trip and a slow server.</summary>
    private static readonly TimeSpan PollGrace = TimeSpan.FromSeconds(30);

    /// <summary>The budget for an outcome report, which may carry a large artifact.</summary>
    private static readonly TimeSpan OutcomeTimeout = TimeSpan.FromMinutes(2);

    private readonly HttpClient _http;

    /// <summary>Creates a transport aimed at <paramref name="baseUrl"/> presenting <paramref name="bearerToken"/>.
    /// <paramref name="handler"/> lets a test host substitute its in-memory server; production passes none.</summary>
    public HttpNodeTransport(Uri baseUrl, string bearerToken, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        // The HttpClient's own timeout is infinite: each call carries its own budget through a linked token, so a
        // long poll is never cut off by a one-size default.
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = baseUrl;
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
    }

    public async Task<NodePollResponse> PollAsync(NodePollRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var budget = TimeSpan.FromSeconds(Math.Max(0, request.WaitSeconds)) + PollGrace;
        using var response = await PostAsync(NodeProtocol.RoutePrefix + "/poll", request, budget, ct).ConfigureAwait(false);
        return await ReadAsync<NodePollResponse>(response, ct).ConfigureAwait(false);
    }

    public async Task<RunOutcomeStatus> ReportRunOutcomeAsync(Guid runId, RunOutcomeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = string.Create(CultureInfo.InvariantCulture, $"{NodeProtocol.RoutePrefix}/runs/{runId}/outcome");
        using var response = await PostAsync(path, request, OutcomeTimeout, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return RunOutcomeStatus.StaleClaim;
        }

        var body = await ReadAsync<RunOutcomeResponse>(response, ct).ConfigureAwait(false);
        return body.Status;
    }

    public async Task<bool> ReportTaskOutcomeAsync(Guid taskId, TaskOutcomeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = string.Create(CultureInfo.InvariantCulture, $"{NodeProtocol.RoutePrefix}/tasks/{taskId}/outcome");
        using var response = await PostAsync(path, request, OutcomeTimeout, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        var body = await ReadAsync<TaskOutcomeResponse>(response, ct).ConfigureAwait(false);
        return body.Recorded;
    }

    private async Task<HttpResponseMessage> PostAsync<T>(string path, T payload, TimeSpan budget, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(budget);
        HttpResponseMessage response;
        try
        {
            response = await _http
                .PostAsJsonAsync(new Uri(path, UriKind.Relative), payload, Json, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new NodeTransportException(
                $"the control plane did not answer {path} within {budget.TotalSeconds:0}s.", null, retryable: true);
        }
        catch (HttpRequestException ex)
        {
            throw new NodeTransportException(
                $"the control plane could not be reached at {_http.BaseAddress}{path.TrimStart('/')}: {ex.Message}", null, retryable: true);
        }

        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
        {
            return response;
        }

        var status = (int)response.StatusCode;
        var detail = await ReadProblemDetailAsync(response, ct).ConfigureAwait(false);
        response.Dispose();
        var retryable = status == StatusCodes.ServiceUnavailable || status >= 500 || status == StatusCodes.TooManyRequests;
        throw new NodeTransportException($"the control plane answered {path} with {status}: {detail}", status, retryable);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false);
        return body ?? throw new NodeTransportException(
            "the control plane answered with an empty body.", (int)response.StatusCode, retryable: true);
    }

    private static async Task<string> ReadProblemDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                return response.ReasonPhrase ?? "(no detail)";
            }

            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (document.RootElement.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                {
                    return detail.GetString() ?? text;
                }

                if (document.RootElement.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                {
                    return title.GetString() ?? text;
                }
            }

            return text.Length > 500 ? text[..500] : text;
        }
        catch (JsonException)
        {
            return response.ReasonPhrase ?? "(unreadable detail)";
        }
    }

    public void Dispose() => _http.Dispose();

    private static class StatusCodes
    {
        public const int TooManyRequests = 429;

        public const int ServiceUnavailable = 503;
    }
}
