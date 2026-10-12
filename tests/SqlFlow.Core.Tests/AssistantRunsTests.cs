using System.Runtime.CompilerServices;
using SqlFlow.Assistant;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// One question's run, whatever the provider: in the route its conversation's mode gives, and, when a data-mode run
/// ends data mode because a follow-up turned to the estate, once more in estate mode, with hosts told to drop what the
/// first attempt wrote. A run already in estate mode is never asked again.
/// </summary>
public sealed class AssistantRunsTests
{
    [Fact]
    public async Task ADataRunThatEndsDataMode_IsAskedAgainInEstateMode_AndTheHostIsTold()
    {
        var settings = AssistantModesTests.Settings(AssistantSurface.Gui);
        using var instructions = AssistantModesTests.Instructions(settings, "SERVER RULES.");
        var router = new AssistantRouter(settings, instructions);
        var attempts = new List<AssistantRoute>();

        var events = await Drain(AssistantRuns.StreamAsync(
            router, settings,
            AssistantModesTests.Request("which flow loads that table?", [new(false, "!how many customers"), new(true, "1,204.")]),
            (route, ct) => Attempt(attempts, route, ct), default));

        Assert.Equal([AssistantMode.Data, AssistantMode.Estate], attempts.Select(route => route.Mode));
        Assert.Contains("mode=estate", attempts[1].ServerUri.Query, StringComparison.Ordinal);
        Assert.Collection(events,
            evt => Assert.Equal(new AssistantEvent.TextDelta("Let me "), evt),
            evt => Assert.Equal(new AssistantEvent.ToolCall(AssistantRuns.EndDataModeTool, AssistantToolCallStatus.Started), evt),
            evt => Assert.Equal(new AssistantEvent.Restarted(AssistantMode.Estate), evt),
            evt => Assert.Equal(new AssistantEvent.ToolCall("describe_object_refresh", AssistantToolCallStatus.Started), evt),
            evt => Assert.Equal(new AssistantEvent.TextDelta("orders_ingest loads it."), evt),
            evt => Assert.Equal(new AssistantEvent.Completed("orders_ingest loads it."), evt));
    }

    [Fact]
    public async Task ARunInEstateMode_IsNeverAskedAgain()
    {
        var settings = AssistantModesTests.Settings(AssistantSurface.Slack);
        using var instructions = AssistantModesTests.Instructions(settings, "SERVER RULES.");
        var attempts = new List<AssistantRoute>();

        var events = await Drain(AssistantRuns.StreamAsync(
            new AssistantRouter(settings, instructions), settings,
            AssistantModesTests.Request("which flow loads the sales table?", []),
            (route, ct) => Attempt(attempts, route, ct), default));

        Assert.Equal([AssistantMode.Estate], attempts.Select(route => route.Mode));
        Assert.DoesNotContain(events, evt => evt is AssistantEvent.Restarted);
        Assert.IsType<AssistantEvent.Completed>(events[^1]);
    }

    [Fact]
    public async Task TheBufferedAnswer_IsTheCompletedTextOfTheLastAttempt()
    {
        var settings = AssistantModesTests.Settings(AssistantSurface.Slack);
        using var instructions = AssistantModesTests.Instructions(settings, "SERVER RULES.");
        var request = AssistantModesTests.Request("and where does that table come from?", [new(false, "!revenue by region")]);

        var answer = await AssistantRuns.AskAsync(
            AssistantRuns.StreamAsync(new AssistantRouter(settings, instructions), settings, request,
                (route, ct) => Attempt([], route, ct), default),
            request, settings, "Test", default);

        Assert.Equal("orders_ingest loads it.", answer);
    }

    [Fact]
    public async Task ARunPastItsDeadline_IsReportedAsATimeout_NamingTheProvider()
    {
        var settings = AssistantModesTests.Settings(AssistantSurface.Slack);
        settings.RunTimeoutSeconds = 1;
        using var instructions = AssistantModesTests.Instructions(settings, "SERVER RULES.");
        var request = AssistantModesTests.Request("why did the load fail?", []);

        var timedOut = await Assert.ThrowsAsync<TimeoutException>(() => AssistantRuns.AskAsync(
            AssistantRuns.StreamAsync(new AssistantRouter(settings, instructions), settings, request, Hang, default),
            request, settings, "Test", default));

        Assert.Contains("Test run for c-1 exceeded 1s", timedOut.Message, StringComparison.Ordinal);
    }

    /// <summary>A data-mode attempt writes a little, then ends data mode (and would go on, were it not dropped); an
    /// estate attempt looks the table up and answers.</summary>
    private static async IAsyncEnumerable<AssistantEvent> Attempt(
        List<AssistantRoute> attempts, AssistantRoute route, [EnumeratorCancellation] CancellationToken ct)
    {
        attempts.Add(route);
        await Task.Yield();
        if (route.Mode == AssistantMode.Data)
        {
            yield return new AssistantEvent.TextDelta("Let me ");
            yield return new AssistantEvent.ToolCall(AssistantRuns.EndDataModeTool, AssistantToolCallStatus.Started);
            yield return new AssistantEvent.TextDelta("never seen");
            yield return new AssistantEvent.Completed("never seen");
            yield break;
        }
        ct.ThrowIfCancellationRequested();
        yield return new AssistantEvent.ToolCall("describe_object_refresh", AssistantToolCallStatus.Started);
        yield return new AssistantEvent.TextDelta("orders_ingest loads it.");
        yield return new AssistantEvent.Completed("orders_ingest loads it.");
    }

    private static async IAsyncEnumerable<AssistantEvent> Hang(AssistantRoute route, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        yield return new AssistantEvent.Completed(route.Mode.ToString());
    }

    private static async Task<List<AssistantEvent>> Drain(IAsyncEnumerable<AssistantEvent> stream)
    {
        var events = new List<AssistantEvent>();
        await foreach (var evt in stream)
        {
            events.Add(evt);
        }
        return events;
    }
}
