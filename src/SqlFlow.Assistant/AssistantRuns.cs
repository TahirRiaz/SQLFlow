using System.Runtime.CompilerServices;

namespace SqlFlow.Assistant;

/// <summary>
/// How one question runs, whatever the provider: under one deadline, in the route its conversation's mode gives, and
/// once more in the other mode when the run switches. A provider's MCP connector opens a session for each request and
/// cannot take a new tool list mid-answer, so when the model calls <see cref="StartDataModeTool"/> (the person asked
/// for data mode in words) or <see cref="EndDataModeTool"/> (<c>!exit</c> in words, or a follow-up turned to the
/// estate), the run is dropped and the question asked again with the other mode's tools; hosts are told with
/// <see cref="AssistantEvent.Restarted"/> to drop what the first attempt wrote. The completed answer carries the mode
/// it ended in, which a host records so the next message starts from it.
/// </summary>
public static class AssistantRuns
{
    /// <summary>The MCP server's tool for switching to data mode, listed in an estate-mode session.</summary>
    public const string StartDataModeTool = "start_data_mode";

    /// <summary>The MCP server's tool for leaving data mode, listed in a data-mode session.</summary>
    public const string EndDataModeTool = "end_data_mode";

    /// <summary>The mode <paramref name="tool"/> switches a conversation to, when it is one of the switches.</summary>
    public static AssistantMode? SwitchesTo(string tool) => tool switch
    {
        StartDataModeTool => AssistantMode.Data,
        EndDataModeTool => AssistantMode.Estate,
        _ => null,
    };

    /// <summary>
    /// Streams one question through <paramref name="attempt"/>, the provider's own run of a route. The deadline
    /// (<see cref="AssistantSettings.RunTimeoutSeconds"/>) covers the whole question, a restart included; a question is
    /// asked again at most once, so a model that keeps switching cannot loop. A switch after that one restart is not
    /// acted on in this answer, which its session cannot take, but the answer is recorded in the mode it switched to,
    /// so the next message starts there.
    /// </summary>
    public static async IAsyncEnumerable<AssistantEvent> StreamAsync(
        AssistantRouter router,
        AssistantSettings settings,
        AssistantRequest request,
        Func<AssistantRoute, CancellationToken, IAsyncEnumerable<AssistantEvent>> attempt,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(attempt);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        runCts.CancelAfter(TimeSpan.FromSeconds(settings.RunTimeoutSeconds));
        var token = runCts.Token;

        var route = await router.ForAsync(request, token).ConfigureAwait(false);
        var restarted = false;
        while (true)
        {
            AssistantMode? switchedTo = null;
            var endedIn = route.Mode;
            await foreach (var evt in attempt(route, token).WithCancellation(token).ConfigureAwait(false))
            {
                if (evt is AssistantEvent.Completed completed)
                {
                    yield return completed with { Mode = endedIn };
                    continue;
                }

                yield return evt;
                if (evt is AssistantEvent.ToolCall { Status: AssistantToolCallStatus.Started } call
                    && SwitchesTo(call.Name) is { } target
                    && target != endedIn)
                {
                    if (restarted)
                    {
                        endedIn = target;
                        continue;
                    }

                    // Leaving the loop disposes the attempt, which closes its stream: nothing it would still write
                    // could use the other mode's tools, which its session does not list.
                    switchedTo = target;
                    break;
                }
            }
            if (switchedTo is not { } mode)
            {
                yield break;
            }

            restarted = true;
            route = await router.InAsync(mode, request.McpBearer, token).ConfigureAwait(false);
            yield return new AssistantEvent.Restarted(mode);
        }
    }

    /// <summary>The buffered form of a stream: the completed answer, with the mode it ended in. A cancellation that is
    /// not the caller's is the run's own deadline, reported as a <see cref="TimeoutException"/> naming
    /// <paramref name="provider"/>.</summary>
    public static async Task<AssistantAnswer> AskAsync(
        IAsyncEnumerable<AssistantEvent> stream, AssistantRequest request, AssistantSettings settings, string provider,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            AssistantAnswer? answer = null;
            await foreach (var evt in stream.WithCancellation(ct).ConfigureAwait(false))
            {
                if (evt is AssistantEvent.Completed completed)
                {
                    answer = new AssistantAnswer(completed.Text, completed.Mode);
                }
            }
            return answer ?? throw new InvalidOperationException(
                $"{provider} stream for {request.ConversationKey} ended without a completed answer.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The {provider} run for {request.ConversationKey} exceeded {settings.RunTimeoutSeconds}s and was cancelled.");
        }
    }
}
