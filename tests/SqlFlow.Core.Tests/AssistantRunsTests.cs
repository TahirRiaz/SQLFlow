using System.Runtime.CompilerServices;
using SqlFlow.Assistant;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// One question's run, whatever the provider: in the route its conversation's mode gives, and, when the run switches
/// mode (start_data_mode from estate mode, end_data_mode from data mode), once more in the other mode, with hosts told
/// to drop what the first attempt wrote. A question is asked again at most once, and the answer carries the mode it
/// ended in, which the host records so the next message starts there.
/// </summary>
public sealed class AssistantRunsTests
{
    private static readonly Dictionary<AssistantMode, string> EndsDataMode = new()
    {
        [AssistantMode.Data] = AssistantRuns.EndDataModeTool,
    };

    private static readonly Dictionary<AssistantMode, string> StartsDataMode = new()
    {
        [AssistantMode.Estate] = AssistantRuns.StartDataModeTool,
    };

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
            (route, ct) => Attempt(attempts, EndsDataMode, route, ct), default));

        Assert.Equal([AssistantMode.Data, AssistantMode.Estate], attempts.Select(route => route.Mode));
        Assert.Contains("mode=estate", attempts[1].ServerUri.Query, StringComparison.Ordinal);
        Assert.Collection(events,
            evt => Assert.Equal(new AssistantEvent.TextDelta("Answered "), evt),
            evt => Assert.Equal(new AssistantEvent.ToolCall(AssistantRuns.EndDataModeTool, AssistantToolCallStatus.Started), evt),
            evt => Assert.Equal(new AssistantEvent.Restarted(AssistantMode.Estate), evt),
            evt => Assert.Equal(new AssistantEvent.TextDelta("Answered "), evt),
            evt => Assert.Equal(new AssistantEvent.TextDelta("in Estate."), evt),
            evt => Assert.Equal(new AssistantEvent.Completed("Answered in Estate.") { Mode = AssistantMode.Estate }, evt));
    }

    [Fact]
    public async Task AnEstateRunThatStartsDataMode_IsAskedAgainInDataMode_AndEndsInIt()
    {
        // Where a leading `!` never reaches the model, the person asks in words and the model calls start_data_mode.
        var settings = AssistantModesTests.Settings(AssistantSurface.Slack);
        using var instructions = AssistantModesTests.Instructions(settings, "SERVER RULES.");
        var attempts = new List<AssistantRoute>();

        var events = await Drain(AssistantRuns.StreamAsync(
            new AssistantRouter(settings, instructions), settings,
            AssistantModesTests.Request("switch to data mode and count the customers", []),
            (route, ct) => Attempt(attempts, StartsDataMode, route, ct), default));

        Assert.Equal([AssistantMode.Estate, AssistantMode.Data], attempts.Select(route => route.Mode));
        Assert.Contains("mode=data", attempts[1].ServerUri.Query, StringComparison.Ordinal);
        Assert.Equal(new AssistantEvent.Restarted(AssistantMode.Data), Assert.Single(events, evt => evt is AssistantEvent.Restarted));
        Assert.Equal(new AssistantEvent.Completed("Answered in Data.") { Mode = AssistantMode.Data }, events[^1]);
    }

    [Fact]
    public async Task AQuestionIsAskedAgainAtMostOnce_AndALaterSwitchStillMovesTheModeItEndsIn()
    {
        var settings = AssistantModesTests.Settings(AssistantSurface.Gui);
        using var instructions = AssistantModesTests.Instructions(settings, "SERVER RULES.");
        var attempts = new List<AssistantRoute>();
        var flips = new Dictionary<AssistantMode, string>
        {
            [AssistantMode.Estate] = AssistantRuns.StartDataModeTool,
            [AssistantMode.Data] = AssistantRuns.EndDataModeTool,
        };

        var events = await Drain(AssistantRuns.StreamAsync(
            new AssistantRouter(settings, instructions), settings,
            AssistantModesTests.Request("data mode please, then which flow loads the customers table?", []),
            (route, ct) => Attempt(attempts, flips, route, ct), default));

        // The second attempt's switch is not acted on in this answer, but the next message starts in estate mode.
        Assert.Equal([AssistantMode.Estate, AssistantMode.Data], attempts.Select(route => route.Mode));
        Assert.Single(events, evt => evt is AssistantEvent.Restarted);
        Assert.Contains(new AssistantEvent.ToolCall(AssistantRuns.EndDataModeTool, AssistantToolCallStatus.Started), events);
        Assert.Equal(new AssistantEvent.Completed("Answered in Data.") { Mode = AssistantMode.Estate }, events[^1]);
    }

    [Fact]
    public async Task ASwitchToTheModeTheRunIsIn_IsNoRestart()
    {
        var settings = AssistantModesTests.Settings(AssistantSurface.Gui);
        using var instructions = AssistantModesTests.Instructions(settings, "SERVER RULES.");
        var attempts = new List<AssistantRoute>();
        var again = new Dictionary<AssistantMode, string> { [AssistantMode.Data] = AssistantRuns.StartDataModeTool };

        var events = await Drain(AssistantRuns.StreamAsync(
            new AssistantRouter(settings, instructions), settings,
            AssistantModesTests.Request("and by region?", [new(false, "!revenue"), new(true, "1.2M.", AssistantMode.Data)]),
            (route, ct) => Attempt(attempts, again, route, ct), default));

        Assert.Equal([AssistantMode.Data], attempts.Select(route => route.Mode));
        Assert.DoesNotContain(events, evt => evt is AssistantEvent.Restarted);
        Assert.Equal(new AssistantEvent.Completed("Answered in Data.") { Mode = AssistantMode.Data }, events[^1]);
    }

    [Fact]
    public async Task ARunThatDoesNotSwitch_IsNeverAskedAgain()
    {
        var settings = AssistantModesTests.Settings(AssistantSurface.Slack);
        using var instructions = AssistantModesTests.Instructions(settings, "SERVER RULES.");
        var attempts = new List<AssistantRoute>();

        var events = await Drain(AssistantRuns.StreamAsync(
            new AssistantRouter(settings, instructions), settings,
            AssistantModesTests.Request("which flow loads the sales table?", []),
            (route, ct) => Attempt(attempts, new Dictionary<AssistantMode, string>(), route, ct), default));

        Assert.Equal([AssistantMode.Estate], attempts.Select(route => route.Mode));
        Assert.DoesNotContain(events, evt => evt is AssistantEvent.Restarted);
        Assert.Equal(new AssistantEvent.Completed("Answered in Estate.") { Mode = AssistantMode.Estate }, events[^1]);
    }

    [Fact]
    public async Task TheBufferedAnswer_IsTheCompletedTextOfTheLastAttempt_WithTheModeItEndedIn()
    {
        var settings = AssistantModesTests.Settings(AssistantSurface.Slack);
        using var instructions = AssistantModesTests.Instructions(settings, "SERVER RULES.");
        var request = AssistantModesTests.Request("and where does that table come from?", [new(false, "!revenue by region")]);

        var answer = await AssistantRuns.AskAsync(
            AssistantRuns.StreamAsync(new AssistantRouter(settings, instructions), settings, request,
                (route, ct) => Attempt([], EndsDataMode, route, ct), default),
            request, settings, "Test", default);

        Assert.Equal(new AssistantAnswer("Answered in Estate.", AssistantMode.Estate), answer);
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

    /// <summary>An attempt writes the start of its answer, calls the switch tool <paramref name="switches"/> names for
    /// its mode when there is one (and would go on, were it not dropped), then finishes the answer, naming its mode.</summary>
    private static async IAsyncEnumerable<AssistantEvent> Attempt(
        List<AssistantRoute> attempts, IReadOnlyDictionary<AssistantMode, string> switches, AssistantRoute route,
        [EnumeratorCancellation] CancellationToken ct)
    {
        attempts.Add(route);
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return new AssistantEvent.TextDelta("Answered ");
        if (switches.TryGetValue(route.Mode, out var tool))
        {
            yield return new AssistantEvent.ToolCall(tool, AssistantToolCallStatus.Started);
        }
        var rest = $"in {route.Mode}.";
        yield return new AssistantEvent.TextDelta(rest);
        yield return new AssistantEvent.Completed("Answered " + rest);
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
