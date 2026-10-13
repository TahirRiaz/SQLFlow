using System.Runtime.CompilerServices;
using SqlFlow.Assistant;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// One question's run, whatever the provider: once, in the route its conversation's mode gives, which the person's
/// commands set before the run starts. Nothing the model does in the run moves it, a call of a switch tool included (the
/// MCP server refuses it in a session its client named), and the answer carries the run's mode, which the host records
/// so the next message starts there.
/// </summary>
public sealed class AssistantRunsTests
{
    [Fact]
    public async Task AFollowUp_RunsOnce_InTheModeThePersonsLastCommandSet()
    {
        var settings = AssistantModesTests.Settings(AssistantSurface.Gui);
        using var instructions = AssistantModesTests.Instructions(settings, "SERVER RULES.");
        var attempts = new List<AssistantRoute>();

        var events = await Drain(AssistantRuns.StreamAsync(
            new AssistantRouter(settings, instructions), settings,
            AssistantModesTests.Request("and by region?", [new(false, "!how many customers"), new(true, "1,204.")]),
            (route, ct) => Attempt(attempts, [], route, ct), default));

        var route = Assert.Single(attempts);
        Assert.Equal(AssistantMode.Data, route.Mode);
        Assert.Contains("mode=data", route.ServerUri.Query, StringComparison.Ordinal);
        Assert.Collection(events,
            evt => Assert.Equal(new AssistantEvent.TextDelta("Answered "), evt),
            evt => Assert.Equal(new AssistantEvent.TextDelta("in Data."), evt),
            evt => Assert.Equal(new AssistantEvent.Completed("Answered in Data.") { Mode = AssistantMode.Data }, evt));
    }

    [Theory]
    [InlineData("which flow loads that table?", "start_data_mode")]
    [InlineData("which flow loads that table?", "end_data_mode")]
    [InlineData("!exit which flow loads that table?", "start_data_mode")]
    [InlineData("!data revenue by region", "end_data_mode")]
    public async Task AModelThatCallsASwitch_NeverMovesTheRun(string question, string switchTool)
    {
        // A data-mode conversation, whatever the question reads like; the person's own command alone decides.
        var settings = AssistantModesTests.Settings(AssistantSurface.Gui);
        using var instructions = AssistantModesTests.Instructions(settings, "SERVER RULES.");
        var attempts = new List<AssistantRoute>();
        ConversationTurn[] prior = [new(false, "!how many customers"), new(true, "1,204.", AssistantMode.Data)];
        var expected = AssistantQuestions.ModeOf(question, prior);

        var events = await Drain(AssistantRuns.StreamAsync(
            new AssistantRouter(settings, instructions), settings,
            AssistantModesTests.Request(question, prior),
            (route, ct) => Attempt(attempts, [switchTool, switchTool], route, ct), default));

        Assert.Equal([expected], attempts.Select(route => route.Mode));
        Assert.Equal(2, events.Count(evt => evt is AssistantEvent.ToolCall { Status: AssistantToolCallStatus.Started }));
        Assert.Equal(new AssistantEvent.Completed($"Answered in {expected}.") { Mode = expected }, events[^1]);
    }

    [Theory]
    [InlineData("switch to data mode and count the customers")]
    [InlineData("please answer this from the data: how many customers?")]
    [InlineData("what is our revenue by region")]
    public async Task AQuestionAskingForTheDataInWords_StaysInEstateMode(string question)
    {
        var settings = AssistantModesTests.Settings(AssistantSurface.Slack);
        using var instructions = AssistantModesTests.Instructions(settings, "SERVER RULES.");
        var attempts = new List<AssistantRoute>();

        var events = await Drain(AssistantRuns.StreamAsync(
            new AssistantRouter(settings, instructions), settings,
            AssistantModesTests.Request(question, []),
            (route, ct) => Attempt(attempts, ["start_data_mode"], route, ct), default));

        Assert.Equal([AssistantMode.Estate], attempts.Select(route => route.Mode));
        Assert.Contains("mode=estate", attempts[0].ServerUri.Query, StringComparison.Ordinal);
        Assert.Equal(AssistantMode.Estate, Assert.IsType<AssistantEvent.Completed>(events[^1]).Mode);
    }

    [Fact]
    public async Task TheCompletedAnswer_CarriesTheRunsMode_WhateverTheProviderSaid()
    {
        var settings = AssistantModesTests.Settings(AssistantSurface.Gui);
        using var instructions = AssistantModesTests.Instructions(settings, "SERVER RULES.");

        var events = await Drain(AssistantRuns.StreamAsync(
            new AssistantRouter(settings, instructions), settings,
            AssistantModesTests.Request("!data revenue", []),
            (route, ct) => Claims(AssistantMode.Estate, ct), default));

        Assert.Equal(new AssistantEvent.Completed("claimed") { Mode = AssistantMode.Data }, Assert.Single(events));
    }

    [Fact]
    public async Task TheBufferedAnswer_IsTheCompletedText_WithTheRunsMode()
    {
        var settings = AssistantModesTests.Settings(AssistantSurface.Slack);
        using var instructions = AssistantModesTests.Instructions(settings, "SERVER RULES.");
        var request = AssistantModesTests.Request("and where does that table come from?", [new(false, "!revenue by region")]);

        var answer = await AssistantRuns.AskAsync(
            AssistantRuns.StreamAsync(new AssistantRouter(settings, instructions), settings, request,
                (route, ct) => Attempt([], ["end_data_mode"], route, ct), default),
            request, settings, "Test", default);

        Assert.Equal(new AssistantAnswer("Answered in Data.", AssistantMode.Data), answer);
    }

    [Fact]
    public async Task AStreamWithoutACompletedAnswer_IsReportedNamingTheConversation()
    {
        var settings = AssistantModesTests.Settings(AssistantSurface.Slack);
        using var instructions = AssistantModesTests.Instructions(settings, "SERVER RULES.");
        var request = AssistantModesTests.Request("why did the load fail?", []);

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => AssistantRuns.AskAsync(
            AssistantRuns.StreamAsync(new AssistantRouter(settings, instructions), settings, request, NeverCompletes, default),
            request, settings, "Test", default));

        Assert.Contains("c-1 ended without a completed answer", failed.Message, StringComparison.Ordinal);
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

    /// <summary>An attempt writes the start of its answer, starts each tool in <paramref name="calls"/> (a switch among
    /// them, which the run never acts on), then finishes the answer, naming its mode.</summary>
    private static async IAsyncEnumerable<AssistantEvent> Attempt(
        List<AssistantRoute> attempts, IReadOnlyList<string> calls, AssistantRoute route,
        [EnumeratorCancellation] CancellationToken ct)
    {
        attempts.Add(route);
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return new AssistantEvent.TextDelta("Answered ");
        foreach (var tool in calls)
        {
            yield return new AssistantEvent.ToolCall(tool, AssistantToolCallStatus.Started);
            yield return new AssistantEvent.ToolCall(tool, AssistantToolCallStatus.Failed);
        }
        var rest = $"in {route.Mode}.";
        yield return new AssistantEvent.TextDelta(rest);
        yield return new AssistantEvent.Completed("Answered " + rest);
    }

    /// <summary>A provider that completes claiming <paramref name="mode"/>, which the run does not take.</summary>
    private static async IAsyncEnumerable<AssistantEvent> Claims(AssistantMode mode, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return new AssistantEvent.Completed("claimed") { Mode = mode };
    }

    private static async IAsyncEnumerable<AssistantEvent> NeverCompletes(AssistantRoute route, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return new AssistantEvent.TextDelta($"Partial in {route.Mode}");
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
