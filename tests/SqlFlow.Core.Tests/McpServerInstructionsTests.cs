using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Assistant;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The MCP server's instructions as a gateway reads them: from the server's own initialize answer in the run's mode,
/// whichever way the server frames it, with the run's bearer, each mode's kept a few minutes, and the session the read
/// opened closed again. A server that cannot be read fails the question with a reason that names the server and never
/// the credential.
/// </summary>
public sealed class McpServerInstructionsTests
{
    private const string Answer = """{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-03-26","instructions":"SERVER RULES."}}""";

    [Fact]
    public async Task APlainJsonAnswer_IsRead_WithTheRunsBearer_AtTheAssistantsAddress()
    {
        var server = new McpServerStub(_ => McpServerStub.Json(Answer));
        using var instructions = Reader(server, new ManualClock());

        Assert.Equal("SERVER RULES.", await instructions.GetAsync(AssistantMode.Data, "delegated-token", default));

        var (method, uri, authorization, _, body) = Assert.Single(server.Received);
        Assert.Equal(HttpMethod.Post, method);
        // The address a data-mode run's session opens at, so the server answers with data mode's instructions.
        Assert.Equal("https://mcp.example/mcp?surface=assistant&mode=data", uri.AbsoluteUri);
        Assert.Equal("Bearer delegated-token", authorization);
        Assert.Contains("\"method\":\"initialize\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEventStreamAnswer_IsRead_AndTheSessionItOpened_IsClosed()
    {
        var server = new McpServerStub(request => request.Method == HttpMethod.Delete
            ? new HttpResponseMessage(HttpStatusCode.Accepted)
            : McpServerStub.Events(
                "event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/message\"}\n\n"
                + $"event: message\ndata: {Answer}\n\n",
                session: "session-7"));
        using var instructions = Reader(server, new ManualClock());

        Assert.Equal("SERVER RULES.", await instructions.GetAsync(AssistantMode.Estate, "delegated-token", default));

        Assert.Equal(2, server.Received.Count);
        var closed = server.Received[1];
        Assert.Equal(HttpMethod.Delete, closed.Method);
        Assert.Equal("session-7", closed.Session);
        Assert.Equal("Bearer delegated-token", closed.Authorization);
    }

    [Fact]
    public async Task TheInstructionsAreKept_ForAFewMinutes_ThenReadAgain()
    {
        var reads = 0;
        var server = new McpServerStub(_ =>
        {
            reads++;
            return McpServerStub.Json(Answer.Replace("SERVER RULES.", $"SERVER RULES {reads}.", StringComparison.Ordinal));
        });
        var clock = new ManualClock();
        using var instructions = Reader(server, clock);

        Assert.Equal("SERVER RULES 1.", await instructions.GetAsync(AssistantMode.Estate, "a", default));
        clock.Advance(McpServerInstructions.CacheFor - TimeSpan.FromSeconds(1));
        Assert.Equal("SERVER RULES 1.", await instructions.GetAsync(AssistantMode.Estate, "b", default));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("SERVER RULES 2.", await instructions.GetAsync(AssistantMode.Estate, "c", default));
        Assert.Equal(2, reads);
    }

    /// <summary>
    /// Each mode's instructions are their own: read at that mode's address, and kept apart, so a data question is
    /// never answered with the estate's rules or the other way round, and each mode is read once per
    /// <see cref="McpServerInstructions.CacheFor"/>.
    /// </summary>
    [Fact]
    public async Task EachModesInstructions_AreReadAtItsAddress_AndKeptApart()
    {
        var server = new McpServerStub(request => McpServerStub.Json(Answer.Replace(
            "SERVER RULES.",
            request.RequestUri!.Query.Contains("mode=data", StringComparison.Ordinal) ? "DATA RULES." : "ESTATE RULES.",
            StringComparison.Ordinal)));
        using var instructions = Reader(server, new ManualClock());

        Assert.Equal("DATA RULES.", await instructions.GetAsync(AssistantMode.Data, "a", default));
        Assert.Equal("ESTATE RULES.", await instructions.GetAsync(AssistantMode.Estate, "b", default));
        Assert.Equal("DATA RULES.", await instructions.GetAsync(AssistantMode.Data, "c", default));
        Assert.Equal("ESTATE RULES.", await instructions.GetAsync(AssistantMode.Estate, "d", default));

        Assert.Equal(
            ["https://mcp.example/mcp?surface=assistant&mode=data", "https://mcp.example/mcp?surface=assistant&mode=estate"],
            server.Received.Select(r => r.Uri.AbsoluteUri));
    }

    [Fact]
    public async Task AServerThatRefuses_FailsTheQuestion_NamingTheServerAndNotTheCredential()
    {
        var server = new McpServerStub(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var instructions = Reader(server, new ManualClock());

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => instructions.GetAsync(AssistantMode.Estate, "secret-token", default));

        Assert.Contains("https://mcp.example/mcp", refused.Message, StringComparison.Ordinal);
        Assert.Contains("401", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-03-26"}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"result":{"instructions":"   "}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":2,"result":{"instructions":"not ours"}}""")]
    [InlineData("not json")]
    public async Task AnAnswerWithoutInstructions_FailsTheQuestion_RatherThanRunningWithoutRules(string body)
    {
        var server = new McpServerStub(_ => McpServerStub.Json(body));
        using var instructions = Reader(server, new ManualClock());

        await Assert.ThrowsAsync<InvalidOperationException>(() => instructions.GetAsync(AssistantMode.Data, "bearer", default));
    }

    private static McpServerInstructions Reader(McpServerStub server, TimeProvider clock)
        => new(AssistantModesTests.Settings(AssistantSurface.Gui), NullLogger<McpServerInstructions>.Instance, clock, server);
}

/// <summary>A clock a test moves by hand.</summary>
internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
