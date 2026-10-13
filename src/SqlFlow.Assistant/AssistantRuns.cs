using System.Runtime.CompilerServices;

namespace SqlFlow.Assistant;

/// <summary>
/// How one question runs, whatever the provider: under one deadline, in the route its conversation's mode gives. The mode
/// is the person's, read from their commands before the run starts (<see cref="AssistantQuestions.ModeOf"/>), and the
/// MCP address opens the session in it: such a session lists that mode's tools and no switch, and the server refuses a
/// call of the other mode's tools, so nothing the model does in the run moves the conversation. The completed answer
/// carries the mode, which a host records so the next message starts from it.
/// </summary>
public static class AssistantRuns
{
    /// <summary>
    /// Streams one question through <paramref name="attempt"/>, the provider's own run of a route, under the deadline
    /// (<see cref="AssistantSettings.RunTimeoutSeconds"/>). The completed answer is stamped with the route's mode.
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
        await foreach (var evt in attempt(route, token).WithCancellation(token).ConfigureAwait(false))
        {
            yield return evt is AssistantEvent.Completed completed ? completed with { Mode = route.Mode } : evt;
        }
    }

    /// <summary>The buffered form of a stream: the completed answer, with the mode it was given in. A cancellation that is
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
