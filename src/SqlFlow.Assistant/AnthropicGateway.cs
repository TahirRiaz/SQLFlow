using System.Runtime.CompilerServices;
using System.Text;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Logging;

namespace SqlFlow.Assistant;

/// <summary>
/// The bridge to the Anthropic Claude API, via the Messages API's MCP connector: each question is
/// one (or, after a <c>pause_turn</c>, a few chained) streamed <c>POST /v1/messages</c> calls
/// carrying the model, the assistant instructions as the system prompt, and the SQLFlow MCP server as an
/// <c>mcp_servers</c> entry whose tools Claude calls server-side. The run's bearer
/// (<see cref="AssistantRequest.McpBearer"/>) rides as the connector's authorization token, the MCP address opens the
/// session in the conversation's mode (<see cref="AssistantRouter"/>), and the system prompt is the MCP server's own
/// instructions with this surface's rendering added. <see cref="AssistantRuns"/> runs the question. Anthropic keeps no server-side conversation state, so every
/// call sends the transcript (already capped by MaxReplayMessages); the host's record stays the
/// single durable transcript, which also means a host restart loses nothing.
/// </summary>
public sealed class AnthropicGateway : IAssistantGateway, IDisposable
{
    private const string McpBeta = "mcp-client-2025-11-20";

    /// <summary>The server-side MCP tool loop pauses after its iteration cap; each resume re-sends the
    /// conversation with the paused turn appended. Five resumes is far beyond any real SQLFlow question
    /// and bounds a pathological loop.</summary>
    private const int MaxPauseResumes = 5;

    private readonly AnthropicClient _client;
    private readonly AssistantSettings _settings;
    private readonly ILogger<AnthropicGateway> _logger;
    private readonly AssistantRouter _router;

    public AnthropicGateway(AssistantSettings settings, McpServerInstructions instructions, ILogger<AnthropicGateway> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(instructions);
        _settings = settings;
        _logger = logger;
        _router = new AssistantRouter(settings, instructions);
        _client = new AnthropicClient
        {
            ApiKey = settings.Anthropic.ApiKey,
            // One question is one long-running call (the MCP tool loop runs inside it), so the
            // request timeout is the run ceiling plus headroom. The SDK retries 429/5xx itself.
            Timeout = TimeSpan.FromSeconds(settings.RunTimeoutSeconds + 15),
        };
    }

    /// <inheritdoc />
    public Task<AssistantAnswer> AskAsync(AssistantRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return AssistantRuns.AskAsync(StreamAsync(request, ct), request, _settings, "Anthropic", ct);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<AssistantEvent> StreamAsync(AssistantRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return AssistantRuns.StreamAsync(_router, _settings, request, (route, token) => RunAsync(request, route, token), ct);
    }

    /// <summary>One attempt at <paramref name="request"/> in <paramref name="route"/>; a resume after pause_turn keeps
    /// the route.</summary>
    private async IAsyncEnumerable<AssistantEvent> RunAsync(
        AssistantRequest request,
        AssistantRoute route,
        [EnumeratorCancellation] CancellationToken token)
    {
        var messages = BuildMessages(request.PriorTurns, request.Question, request.ImageDataUris);
        var answer = new StringBuilder();

        for (var resumes = 0; ; resumes++)
        {
            // Every raw event is both surfaced live (text deltas, tool transitions) and collected, so
            // the SDK's aggregator can rebuild the completed message afterwards: the stop reason drives
            // the pause_turn resume loop, and a resume must echo the paused turn's blocks byte-faithfully.
            var collected = new List<BetaRawMessageStreamEvent>();
            // Tool-use blocks stream as start/stop pairs identified by index; the name only rides on the
            // start, so it is remembered per index to emit the Completed transition at the stop.
            var openToolCalls = new Dictionary<long, string>();

            await foreach (var raw in _client.Beta.Messages.CreateStreaming(
                BuildParams(messages, request.McpBearer, route), cancellationToken: token).ConfigureAwait(false))
            {
                collected.Add(raw);
                if (raw.TryPickContentBlockDelta(out var deltaEvent)
                    && deltaEvent.Delta.TryPickText(out var textDelta)
                    && textDelta.Text is { Length: > 0 } text)
                {
                    answer.Append(text);
                    yield return new AssistantEvent.TextDelta(text);
                }
                else if (raw.TryPickContentBlockStart(out var startEvent)
                    && startEvent.ContentBlock.TryPickBetaMcpToolUse(out var toolUse))
                {
                    openToolCalls[startEvent.Index] = toolUse.Name;
                    yield return new AssistantEvent.ToolCall(toolUse.Name, AssistantToolCallStatus.Started);
                }
                else if (raw.TryPickContentBlockStop(out var stopEvent)
                    && openToolCalls.Remove(stopEvent.Index, out var finishedTool))
                {
                    // The connector reports a failing tool inside the result block's content, not as a
                    // distinct stop state, so a closed call is surfaced as completed; the model narrates
                    // any tool error in its answer.
                    yield return new AssistantEvent.ToolCall(finishedTool, AssistantToolCallStatus.Completed);
                }
            }

            var response = await ReplayEvents(collected).Aggregate().ConfigureAwait(false);
            var stopReason = response.StopReason?.ToString() ?? "";

            if (string.Equals(stopReason, "pause_turn", StringComparison.OrdinalIgnoreCase))
            {
                if (resumes >= MaxPauseResumes)
                {
                    throw new InvalidOperationException(
                        $"Anthropic run for {request.ConversationKey} was still paused after {MaxPauseResumes} resumes; giving up.");
                }
                // The server-side MCP loop hit its iteration cap mid-turn. Echo the paused assistant
                // turn back verbatim and re-send: the API detects the trailing server-tool state and
                // resumes where it left off. The blocks are echoed as raw JSON deliberately, because a
                // resume must return them byte-faithfully, not rebuilt through typed variants.
                _logger.LogInformation("Anthropic paused the tool loop for {Key}; resuming ({Resume}/{Max})",
                    request.ConversationKey, resumes + 1, MaxPauseResumes);
                messages.Add(new BetaMessageParam
                {
                    Role = Role.Assistant,
                    Content = response.Content.Select(block => new BetaContentBlockParam(block.Json)).ToList(),
                });
                continue;
            }

            if (string.Equals(stopReason, "refusal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Anthropic declined to answer this question (safety refusal). Rephrase the question or contact an admin if it looks wrong.");
            }

            if (answer.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Anthropic response {response.ID} ended as '{stopReason}' but produced no text answer.");
            }
            if (string.Equals(stopReason, "max_tokens", StringComparison.OrdinalIgnoreCase))
            {
                // A truncated answer is still worth delivering; note the cut so the user can ask to continue.
                _logger.LogWarning("Anthropic answer for {Key} hit the {Max}-token output cap and was truncated",
                    request.ConversationKey, _settings.Anthropic.MaxTokens);
                const string note = "\n_(answer hit the output limit; ask me to continue for the rest)_";
                answer.Append(note);
                yield return new AssistantEvent.TextDelta(note);
            }
            yield return new AssistantEvent.Completed(answer.ToString());
            yield break;
        }
    }

    /// <summary>Replays already-received raw events as an async sequence, the shape the SDK's
    /// aggregator consumes to rebuild the completed message.</summary>
    private static async IAsyncEnumerable<BetaRawMessageStreamEvent> ReplayEvents(List<BetaRawMessageStreamEvent> events)
    {
        foreach (var evt in events)
        {
            yield return evt;
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private MessageCreateParams BuildParams(List<BetaMessageParam> messages, string mcpBearer, AssistantRoute route)
    {
        // Every tool the session lists, which the MCP server chose for the session's mode; a deployment that narrowed
        // the tools gets an allowlist instead, everything else off, as the other providers get it via allowed_tools.
        var mcpToolset = route.Tools.Count == 0
            ? new BetaMcpToolset
            {
                McpServerName = _settings.Mcp.ServerLabel,
                DefaultConfig = new BetaMcpToolDefaultConfig { Enabled = true },
            }
            : new BetaMcpToolset
            {
                McpServerName = _settings.Mcp.ServerLabel,
                DefaultConfig = new BetaMcpToolDefaultConfig { Enabled = false },
                Configs = route.Tools.ToDictionary(
                    tool => tool,
                    _ => new BetaMcpToolConfig { Enabled = true },
                    StringComparer.Ordinal),
            };

        return new MessageCreateParams
        {
            Model = _settings.Anthropic.Model,
            MaxTokens = _settings.Anthropic.MaxTokens,
            System = route.Instructions,
            Betas = [McpBeta],
            Thinking = new BetaThinkingConfigAdaptive(),
            McpServers =
            [
                new BetaRequestMcpServerUrlDefinition
                {
                    Name = _settings.Mcp.ServerLabel,
                    Url = route.ServerUri.AbsoluteUri,
                    // Sent by the connector as "Authorization: Bearer <token>"; the MCP server
                    // forwards it verbatim to the control plane, which enforces the token's scopes.
                    AuthorizationToken = mcpBearer,
                },
            ],
            Tools = [new BetaToolUnion(mcpToolset)],
            Messages = messages,
        };
    }

    /// <summary>
    /// The full conversation for one call: the capped transcript, then the new question with its
    /// image attachments. Leading assistant turns are dropped because the Messages API requires
    /// the first message to be a user turn; the last message is always the user question, so the
    /// no-trailing-assistant-prefill rule holds by construction.
    /// </summary>
    private List<BetaMessageParam> BuildMessages(
        IReadOnlyList<ConversationTurn> priorTurns,
        string question,
        IReadOnlyList<string> imageDataUris)
    {
        var messages = new List<BetaMessageParam>();
        foreach (var turn in priorTurns.TakeLast(_settings.MaxReplayMessages))
        {
            if (string.IsNullOrWhiteSpace(turn.Text))
            {
                continue;
            }
            if (messages.Count == 0 && turn.FromAssistant)
            {
                continue;
            }
            messages.Add(new BetaMessageParam
            {
                Role = turn.FromAssistant ? Role.Assistant : Role.User,
                Content = turn.Text,
            });
        }

        if (imageDataUris.Count == 0)
        {
            messages.Add(new BetaMessageParam { Role = Role.User, Content = question });
            return messages;
        }

        var parts = new List<BetaContentBlockParam> { new BetaTextBlockParam { Text = question } };
        foreach (var dataUri in imageDataUris)
        {
            if (TryParseDataUri(dataUri, out var mediaType, out var base64))
            {
                parts.Add(new BetaImageBlockParam
                {
                    Source = new BetaBase64ImageSource { MediaType = mediaType, Data = base64 },
                });
            }
            else
            {
                _logger.LogWarning("Skipping an attachment that is not a base64 image data URI");
            }
        }
        messages.Add(new BetaMessageParam { Role = Role.User, Content = parts });
        return messages;
    }

    /// <summary>Splits a <c>data:&lt;mime&gt;;base64,&lt;payload&gt;</c> URI (the shape every host
    /// produces for attachments) into the media type and payload the Anthropic image source expects.</summary>
    public static bool TryParseDataUri(string dataUri, out string mediaType, out string base64)
    {
        ArgumentNullException.ThrowIfNull(dataUri);
        mediaType = "";
        base64 = "";
        if (!dataUri.StartsWith("data:", StringComparison.Ordinal))
        {
            return false;
        }
        var comma = dataUri.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            return false;
        }
        var header = dataUri[5..comma];
        if (!header.EndsWith(";base64", StringComparison.Ordinal))
        {
            return false;
        }
        mediaType = header[..^";base64".Length];
        base64 = dataUri[(comma + 1)..];
        return mediaType.Length > 0 && base64.Length > 0;
    }

    public void Dispose() => _client.Dispose();
}
