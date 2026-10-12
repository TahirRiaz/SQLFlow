using System.Runtime.CompilerServices;

namespace SqlFlow.Assistant;

/// <summary>
/// How one question runs, whatever the provider: under one deadline, in the route its conversation's mode gives, and
/// once more in estate mode when a data-mode run ends data mode. A provider's MCP connector opens a session for each
/// request and cannot take a new tool list mid-answer, so when the model calls <see cref="EndDataModeTool"/> (a
/// follow-up turned to the estate) the run is dropped and the question asked again with the estate's tools; hosts are
/// told with <see cref="AssistantEvent.Restarted"/> to drop what the first attempt wrote.
/// </summary>
public static class AssistantRuns
{
    /// <summary>The MCP server's tool for leaving data mode, listed only in a data-mode session.</summary>
    public const string EndDataModeTool = "end_data_mode";

    /// <summary>
    /// Streams one question through <paramref name="attempt"/>, the provider's own run of a route. The deadline
    /// (<see cref="AssistantSettings.RunTimeoutSeconds"/>) covers the whole question, a restart included; a run already
    /// in estate mode is never restarted, so a question runs at most twice.
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
        while (true)
        {
            var leftDataMode = false;
            await foreach (var evt in attempt(route, token).WithCancellation(token).ConfigureAwait(false))
            {
                yield return evt;
                if (route.Mode == AssistantMode.Data
                    && evt is AssistantEvent.ToolCall { Name: EndDataModeTool, Status: AssistantToolCallStatus.Started })
                {
                    // Leaving the loop disposes the attempt, which closes its stream: nothing it would still write
                    // could use the estate's tools, which its session does not list.
                    leftDataMode = true;
                    break;
                }
            }
            if (!leftDataMode)
            {
                yield break;
            }

            route = await router.InAsync(AssistantMode.Estate, request.McpBearer, token).ConfigureAwait(false);
            yield return new AssistantEvent.Restarted(AssistantMode.Estate);
        }
    }

    /// <summary>The buffered form of a stream: the completed answer's text. A cancellation that is not the caller's is
    /// the run's own deadline, reported as a <see cref="TimeoutException"/> naming <paramref name="provider"/>.</summary>
    public static async Task<string> AskAsync(
        IAsyncEnumerable<AssistantEvent> stream, AssistantRequest request, AssistantSettings settings, string provider,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            string? answer = null;
            await foreach (var evt in stream.WithCancellation(ct).ConfigureAwait(false))
            {
                if (evt is AssistantEvent.Completed completed)
                {
                    answer = completed.Text;
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
