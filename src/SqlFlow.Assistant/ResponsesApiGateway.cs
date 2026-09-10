using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Microsoft.Extensions.Logging;
using SqlFlow.Azure;

namespace SqlFlow.Assistant;

/// <summary>
/// The bridge to a Responses-API provider: Azure AI Foundry, or the OpenAI platform directly.
/// Both speak the same wire format, so one gateway serves both; only the endpoint, the model
/// name, and the credential differ (Azure token via managed identity for Foundry, a static API
/// key for OpenAI). Each question is one streamed <c>POST .../responses</c> carrying the model,
/// the assistant instructions, the MCP tool (the deployed SQLFlow MCP server, with the caller's
/// bearer as its Authorization header and the tool allowlist), and either the new turn plus a
/// <c>previous_response_id</c> that chains the conversation server-side, or - when that link is
/// lost (host restart, server-side expiry) - the transcript replayed as the input. On Foundry the
/// Responses API supersedes the persistent-agents API for MCP tools: current model deployments
/// (for example gpt-5-mini) support the MCP tool only through this surface; the Foundry identity
/// needs the Cognitive Services OpenAI User role on the account.
/// </summary>
public sealed class ResponsesApiGateway : IAssistantGateway, IDisposable
{
    private const string AzureApiVersion = "2025-04-01-preview";
    private static readonly string[] AzureScopes = ["https://cognitiveservices.azure.com/.default"];

    /// <summary>How many times a throttled (429) or briefly-unavailable (503) request is retried before it
    /// surfaces as an error. Each retry honors the server's Retry-After, or an exponential backoff.
    /// Retries happen only before the first streamed byte, so a consumer never sees a rewound stream.</summary>
    private const int MaxThrottleRetries = 4;

    private readonly HttpClient _http;
    private readonly TokenCredential? _credential;
    private readonly string? _apiKey;
    private readonly string _model;
    private readonly string _providerName;
    private readonly AssistantSettings _settings;
    private readonly ILogger<ResponsesApiGateway> _logger;
    private readonly Uri _endpoint;
    private readonly string _instructions;

    /// <summary>Conversation key to the id of the last Responses API response in that conversation, so a
    /// follow-up chains server-side via previous_response_id. Bounded; an evicted key is rebuilt from the
    /// host's transcript (the durable record), so eviction costs a little latency, never context.</summary>
    private readonly ConcurrentDictionary<string, string> _threadResponses = new(StringComparer.Ordinal);
    private const int MaxCachedThreads = 2000;

    public ResponsesApiGateway(
        AssistantSettings settings,
        IAzureCredentialFactory credentialFactory,
        ILogger<ResponsesApiGateway> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(credentialFactory);
        _settings = settings;
        _logger = logger;
        switch (settings.Provider)
        {
            case AssistantProvider.AzureFoundry:
                _credential = credentialFactory.Create();
                _apiKey = null;
                _endpoint = BuildAzureResponsesEndpoint(settings.Foundry.ProjectEndpoint);
                _model = settings.Foundry.ModelDeploymentName;
                _providerName = "Foundry";
                break;
            case AssistantProvider.OpenAI:
                _credential = null;
                _apiKey = settings.OpenAI.ApiKey;
                _endpoint = new Uri(settings.OpenAI.BaseUrl.TrimEnd('/') + "/v1/responses");
                _model = settings.OpenAI.Model;
                _providerName = "OpenAI";
                break;
            default:
                throw new InvalidOperationException(
                    $"ResponsesApiGateway serves AzureFoundry and OpenAI, not {settings.Provider}.");
        }
        _instructions = AssistantInstructions.Build(settings);
        // Responses stream for the whole agent run, so no per-request client timeout: the run
        // ceiling is enforced with a linked CancellationTokenSource per call.
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <inheritdoc />
    public async Task<string> AskAsync(AssistantRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            string? answer = null;
            await foreach (var evt in StreamAsync(request, ct).ConfigureAwait(false))
            {
                if (evt is AssistantEvent.Completed completed)
                {
                    answer = completed.Text;
                }
            }
            return answer ?? throw new InvalidOperationException(
                $"{_providerName} stream for {request.ConversationKey} ended without a completed answer.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The {_providerName} run for {request.ConversationKey} exceeded {_settings.RunTimeoutSeconds}s and was cancelled.");
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AssistantEvent> StreamAsync(
        AssistantRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        runCts.CancelAfter(TimeSpan.FromSeconds(_settings.RunTimeoutSeconds));
        var token = runCts.Token;

        // A cached previous-response id chains the conversation server-side: send only the new turn.
        // The 404/400 that reveals a lost link arrives before any streamed byte, so falling back to a
        // transcript rebuild never rewinds a stream the consumer already saw.
        HttpResponseMessage? response = null;
        if (_threadResponses.TryGetValue(request.ConversationKey, out var previousId))
        {
            try
            {
                response = await SendAsync(
                    BuildRequestBody(previousId, [new ConversationTurn(false, request.Question)], request.ImageDataUris, request.McpBearer),
                    previousId, token).ConfigureAwait(false);
            }
            catch (ResponseLinkLostException)
            {
                _logger.LogWarning("Previous response {Id} for {Key} is gone; rebuilding from the transcript",
                    previousId, request.ConversationKey);
                _threadResponses.TryRemove(request.ConversationKey, out _);
            }
        }
        if (response is null)
        {
            // Cold conversation (or a lost link): seed the input with the replayed transcript plus the question.
            var seeded = new List<ConversationTurn>(request.PriorTurns.Count + 1);
            seeded.AddRange(request.PriorTurns.TakeLast(_settings.MaxReplayMessages));
            seeded.Add(new ConversationTurn(false, request.Question));
            response = await SendAsync(
                BuildRequestBody(null, seeded, request.ImageDataUris, request.McpBearer),
                previousResponseId: null, token).ConfigureAwait(false);
        }

        using (response)
        {
            var state = new StreamState();
            using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var data = new StringBuilder();
            while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    if (data.Length > 0)
                    {
                        foreach (var evt in HandleServerEvent(request.ConversationKey, data.ToString(), state))
                        {
                            yield return evt;
                        }
                        data.Clear();
                    }
                    continue;
                }
                // Frame fields other than data (event:, id:, comments) carry nothing the payload's own
                // "type" property does not, so only data lines are accumulated (multi-line per the SSE spec).
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0)
                    {
                        data.Append('\n');
                    }
                    data.Append(line[5..].TrimStart());
                }
            }
            if (data.Length > 0)
            {
                foreach (var evt in HandleServerEvent(request.ConversationKey, data.ToString(), state))
                {
                    yield return evt;
                }
            }
            if (!state.Completed)
            {
                throw new InvalidOperationException(
                    $"{_providerName} stream for {request.ConversationKey} ended without a response.completed event.");
            }
        }
    }

    /// <summary>Sends one Responses request and returns the open streaming response. Throttle retries
    /// (429/503) and the lost-chain probe all resolve here, before the first streamed byte.</summary>
    private async Task<HttpResponseMessage> SendAsync(string body, string? previousResponseId, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (_credential is not null)
            {
                var token = await _credential.GetTokenAsync(new TokenRequestContext(AzureScopes), ct).ConfigureAwait(false);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            }
            else
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            }

            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            using var failed = response;
            var payload = await failed.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // The model throttled (429) or is briefly unavailable (503). An agent question is many
            // token-heavy tool-call round-trips, so a burst can trip the per-minute rate limit; wait the
            // server's Retry-After (or an exponential backoff) and retry a bounded number of times, so it
            // recovers on its own instead of surfacing an error to the user.
            if ((failed.StatusCode == HttpStatusCode.TooManyRequests || failed.StatusCode == HttpStatusCode.ServiceUnavailable)
                && attempt < MaxThrottleRetries)
            {
                var delay = RetryDelay(failed, attempt);
                _logger.LogWarning("{Provider} throttled ({Status}) on attempt {Attempt}/{Max}; retrying in {Delay:0.#}s",
                    _providerName, (int)failed.StatusCode, attempt + 1, MaxThrottleRetries, delay.TotalSeconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
                continue;
            }

            // A chained request whose previous_response_id no longer exists comes back 404 (or 400 naming
            // that id); either way the fix is to rebuild from the transcript, which the caller does.
            if (previousResponseId is not null && (failed.StatusCode == HttpStatusCode.NotFound
                || (failed.StatusCode == HttpStatusCode.BadRequest && payload.Contains(previousResponseId, StringComparison.Ordinal))))
            {
                throw new ResponseLinkLostException();
            }

            throw new InvalidOperationException(
                $"{_providerName} Responses API returned {(int)failed.StatusCode} {failed.ReasonPhrase}: {Truncate(payload)}");
        }
    }

    /// <summary>Accumulates one streamed run's state across server events.</summary>
    private sealed class StreamState
    {
        /// <summary>The answer text accumulated from deltas, the fallback when the terminal
        /// response object carries no output text of its own.</summary>
        public StringBuilder Text { get; } = new();

        public bool Completed { get; set; }
    }

    /// <summary>
    /// Maps one server event (a Responses API stream payload) to zero or more assistant events.
    /// Terminal failures (response.failed, response.incomplete, error) throw, matching the
    /// non-streaming contract where any non-completed outcome is an error.
    /// </summary>
    private List<AssistantEvent> HandleServerEvent(string key, string json, StreamState state)
    {
        JsonObject root;
        try
        {
            // The [DONE] sentinel some Responses implementations emit after response.completed is not JSON.
            if (json.Equals("[DONE]", StringComparison.Ordinal))
            {
                return [];
            }
            root = JsonNode.Parse(json)?.AsObject()
                ?? throw new InvalidOperationException($"{_providerName} stream event was not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{_providerName} stream event was not valid JSON: {Truncate(json)}", ex);
        }

        switch ((string?)root["type"])
        {
            case "response.output_text.delta":
                if ((string?)root["delta"] is { Length: > 0 } delta)
                {
                    state.Text.Append(delta);
                    return [new AssistantEvent.TextDelta(delta)];
                }
                return [];

            case "response.output_item.added":
                if (root["item"] is JsonObject added && (string?)added["type"] == "mcp_call"
                    && (string?)added["name"] is { Length: > 0 } startedName)
                {
                    return [new AssistantEvent.ToolCall(startedName, AssistantToolCallStatus.Started)];
                }
                return [];

            case "response.output_item.done":
                if (root["item"] is JsonObject done && (string?)done["type"] == "mcp_call"
                    && (string?)done["name"] is { Length: > 0 } doneName)
                {
                    // A JSON null error and an absent error both surface as a null node; either way the
                    // call succeeded. Any present error value means the model saw the tool fail.
                    var status = done["error"] is null
                        ? AssistantToolCallStatus.Completed
                        : AssistantToolCallStatus.Failed;
                    return [new AssistantEvent.ToolCall(doneName, status)];
                }
                return [];

            case "response.completed":
            {
                if (root["response"] is not JsonObject completed)
                {
                    throw new InvalidOperationException($"{_providerName} response.completed event carried no response object.");
                }
                if ((string?)completed["id"] is { Length: > 0 } id)
                {
                    RememberResponse(key, id);
                }
                var text = ExtractOutputText(completed);
                if (text.Length == 0)
                {
                    text = state.Text.ToString();
                }
                if (text.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"{_providerName} response {(string?)completed["id"] ?? "(no id)"} completed but produced no text answer.");
                }
                state.Completed = true;
                return [new AssistantEvent.Completed(text)];
            }

            case "response.failed":
            case "response.incomplete":
            {
                var detail = root["response"] is JsonObject r
                    ? r["error"]?.ToJsonString() ?? r["incomplete_details"]?.ToJsonString() ?? "no detail"
                    : "no detail";
                var status = (string?)root["type"] == "response.failed" ? "failed" : "incomplete";
                throw new InvalidOperationException($"{_providerName} response ended as '{status}' ({Truncate(detail)}).");
            }

            case "error":
                throw new InvalidOperationException(
                    $"{_providerName} stream reported an error: {Truncate(root["message"]?.ToString() ?? json)}");

            default:
                return [];
        }
    }

    private static string ExtractOutputText(JsonObject response)
    {
        var answer = new StringBuilder();
        if (response["output"] is JsonArray output)
        {
            foreach (var item in output)
            {
                if (item is not JsonObject message || (string?)message["type"] != "message"
                    || message["content"] is not JsonArray content)
                {
                    continue;
                }
                foreach (var part in content)
                {
                    if (part is JsonObject textPart
                        && (string?)textPart["type"] == "output_text"
                        && (string?)textPart["text"] is { Length: > 0 } text)
                    {
                        if (answer.Length > 0)
                        {
                            answer.AppendLine();
                        }
                        answer.Append(text);
                    }
                }
            }
        }
        return answer.ToString();
    }

    private string BuildRequestBody(
        string? previousResponseId,
        IReadOnlyList<ConversationTurn> turns,
        IReadOnlyList<string> imageDataUris,
        string mcpBearer)
    {
        var input = new JsonArray();
        for (var i = 0; i < turns.Count; i++)
        {
            var turn = turns[i];
            // Images ride on the current question, which is always the last, user-authored turn.
            var attachImages = i == turns.Count - 1 && !turn.FromAssistant && imageDataUris.Count > 0;
            if (string.IsNullOrWhiteSpace(turn.Text) && !attachImages)
            {
                continue;
            }

            JsonNode content;
            if (attachImages)
            {
                // A message with images sends structured content: the text (when present) plus one
                // input_image per attachment, so the vision-capable model reads the screenshot alongside
                // the question ("here is the error I got, what does it mean").
                var parts = new JsonArray();
                if (!string.IsNullOrWhiteSpace(turn.Text))
                {
                    parts.Add(new JsonObject { ["type"] = "input_text", ["text"] = turn.Text });
                }
                foreach (var uri in imageDataUris)
                {
                    parts.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = uri });
                }
                content = parts;
            }
            else
            {
                content = JsonValue.Create(turn.Text);
            }

            input.Add(new JsonObject
            {
                ["role"] = turn.FromAssistant ? "assistant" : "user",
                ["content"] = content,
            });
        }

        var mcpTool = new JsonObject
        {
            ["type"] = "mcp",
            ["server_label"] = _settings.Mcp.ServerLabel,
            ["server_url"] = _settings.Mcp.ServerUrl,
            // Approval is off because there is no human in the loop to approve a tool call mid-run;
            // authority is bounded by the bearer's scopes and the tool allowlist instead.
            ["require_approval"] = "never",
            ["headers"] = new JsonObject
            {
                // The MCP server forwards this verbatim to the control plane, which enforces the token's
                // scopes; this bearer is the run's whole authority.
                ["Authorization"] = "Bearer " + mcpBearer,
            },
        };
        if (_settings.Mcp.AllowedTools.Count > 0)
        {
            var allowed = new JsonArray();
            foreach (var tool in _settings.Mcp.AllowedTools)
            {
                allowed.Add(tool);
            }
            mcpTool["allowed_tools"] = allowed;
        }

        var body = new JsonObject
        {
            ["model"] = _model,
            ["instructions"] = _instructions,
            ["input"] = input,
            ["tools"] = new JsonArray { mcpTool },
            ["stream"] = true,
            // Persist the response so a follow-up can chain from it via previous_response_id.
            ["store"] = true,
        };
        if (previousResponseId is not null)
        {
            body["previous_response_id"] = previousResponseId;
        }
        return body.ToJsonString();
    }

    private void RememberResponse(string key, string responseId)
    {
        _threadResponses[key] = responseId;
        // Bounded cache: past the cap, drop an arbitrary batch. Evicted conversations are rebuilt from
        // the host's transcript on next use, so eviction costs a little latency, never context.
        if (_threadResponses.Count > MaxCachedThreads)
        {
            foreach (var stale in _threadResponses.Keys.Take(MaxCachedThreads / 10))
            {
                _threadResponses.TryRemove(stale, out _);
            }
        }
    }

    /// <summary>
    /// Builds the Azure OpenAI Responses endpoint from the Foundry project endpoint: the account is the
    /// first host label of <c>https://&lt;account&gt;.services.ai.azure.com/api/projects/&lt;project&gt;</c>,
    /// and the Responses API lives at <c>https://&lt;account&gt;.openai.azure.com/openai/responses</c>.
    /// </summary>
    private static Uri BuildAzureResponsesEndpoint(string projectEndpoint)
    {
        if (!Uri.TryCreate(projectEndpoint, UriKind.Absolute, out var parsed))
        {
            throw new InvalidOperationException(
                $"Foundry ProjectEndpoint '{projectEndpoint}' is not an absolute URL.");
        }
        var account = parsed.Host.Split('.', 2)[0];
        if (account.Length == 0)
        {
            throw new InvalidOperationException(
                $"Could not read the Foundry account name from ProjectEndpoint '{projectEndpoint}'.");
        }
        return new Uri($"https://{account}.openai.azure.com/openai/responses?api-version={AzureApiVersion}");
    }

    /// <summary>The wait before a throttle retry: the server's Retry-After header when present (a delta or an
    /// HTTP date), otherwise an exponential backoff (2s, 4s, 8s...) capped at 30s so a retry never stalls the
    /// conversation for long.</summary>
    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }
        if (retryAfter?.Date is { } date && date - DateTimeOffset.UtcNow is { } until && until > TimeSpan.Zero)
        {
            return until;
        }
        return TimeSpan.FromSeconds(Math.Min(30, 2 * Math.Pow(2, attempt)));
    }

    private static string Truncate(string value)
        => value.Length <= 600 ? value : value[..600] + "...";

    public void Dispose() => _http.Dispose();

    /// <summary>Raised when a chained request's previous_response_id no longer exists server-side, so the
    /// caller rebuilds the conversation from the host's transcript.</summary>
    private sealed class ResponseLinkLostException : Exception;
}
