namespace SqlFlow.Assistant;

/// <summary>One prior message of a conversation, replayed when the model conversation must be rebuilt.</summary>
/// <param name="FromAssistant">True when the message is an assistant turn (a previous answer).</param>
/// <param name="Text">The message text as the host recorded it.</param>
public readonly record struct ConversationTurn(bool FromAssistant, string Text);

/// <summary>How far a streamed tool call has progressed.</summary>
public enum AssistantToolCallStatus
{
    /// <summary>The model started the tool call; its result is not back yet.</summary>
    Started,

    /// <summary>The tool call finished and its result is in the model's context.</summary>
    Completed,

    /// <summary>The tool call errored; the model saw the error and continues without the result.</summary>
    Failed,
}

/// <summary>
/// One event of a streamed assistant run, in the order the model produced it: text arrives as
/// incremental <see cref="TextDelta"/> fragments, tool activity as <see cref="ToolCall"/>
/// transitions, and exactly one terminal <see cref="Completed"/> carries the full answer (already
/// equal to the concatenated deltas). Failures surface as thrown exceptions, never as events.
/// </summary>
public abstract record AssistantEvent
{
    private AssistantEvent() { }

    /// <summary>The next fragment of the answer text (a delta, not the accumulated text).</summary>
    public sealed record TextDelta(string Text) : AssistantEvent;

    /// <summary>A tool-call status transition, for rendering live tool activity.</summary>
    public sealed record ToolCall(string Name, AssistantToolCallStatus Status) : AssistantEvent;

    /// <summary>The terminal event: the complete answer text.</summary>
    public sealed record Completed(string Text) : AssistantEvent;
}

/// <summary>One question for the assistant, with the conversational context it runs in.</summary>
public sealed class AssistantRequest
{
    /// <summary>
    /// Stable identity of the conversation this question belongs to (a Slack <c>channel:threadTs</c>,
    /// a chat conversation id). Providers with server-side conversation state key their chain cache
    /// on it; an evicted or lost chain is rebuilt from <see cref="PriorTurns"/>, so the host's
    /// transcript stays the single durable record.
    /// </summary>
    public required string ConversationKey { get; init; }

    /// <summary>The conversation's transcript excluding the new question, oldest first. Providers
    /// with server-side state consume it only when rebuilding; stateless providers send it on
    /// every call (capped by <see cref="AssistantSettings.MaxReplayMessages"/>).</summary>
    public required IReadOnlyList<ConversationTurn> PriorTurns { get; init; }

    /// <summary>The new user question.</summary>
    public required string Question { get; init; }

    /// <summary>The question's image attachments as <c>data:&lt;mime&gt;;base64,...</c> URIs, for the
    /// vision model. The host enforces its image count and size caps before building the request.</summary>
    public IReadOnlyList<string> ImageDataUris { get; init; } = [];

    /// <summary>
    /// The bearer token the model provider forwards to the SQLFlow MCP server on every tool call;
    /// the MCP server passes it verbatim to the control plane, which enforces its scopes. The
    /// Slack bot sends its one read-scoped access token; the GUI chat sends the calling user's own
    /// token, so tool access is exactly that user's access.
    /// </summary>
    public required string McpBearer { get; init; }
}

/// <summary>
/// The model-provider boundary: one implementation per <see cref="AssistantProvider"/>, all
/// consuming the same SQLFlow MCP server as their tool source and the same instructions, so the
/// assistant experience is identical regardless of which provider answers. Hosts never know which
/// one they are talking to.
/// </summary>
public interface IAssistantGateway
{
    /// <summary>Answers one question and returns the complete answer text (the buffered
    /// equivalent of draining <see cref="StreamAsync"/>).</summary>
    Task<string> AskAsync(AssistantRequest request, CancellationToken ct);

    /// <summary>
    /// Answers one question as a live event stream: text deltas and tool-call transitions as the
    /// model produces them, then one terminal <see cref="AssistantEvent.Completed"/>. Failures
    /// (provider errors, timeouts, refusals) are thrown from the enumeration.
    /// </summary>
    IAsyncEnumerable<AssistantEvent> StreamAsync(AssistantRequest request, CancellationToken ct);
}
