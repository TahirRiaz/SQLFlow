using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Assistant;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The commands a person moves a conversation between modes with, and the route a run is given for the mode its
/// conversation is in. The MCP server defines the modes and what each one lists; the control plane only reads the
/// commands from the person's own messages and names the mode on the MCP address, so a follow-up never has to repeat
/// the command and an answer that quotes one back never moves the conversation.
/// </summary>
public sealed class AssistantModesTests
{
    [Theory]
    [InlineData("!what is our revenue by region", AssistantMode.Data)]
    [InlineData("  ! how many customers", AssistantMode.Data)]
    [InlineData("!cwd total sales", AssistantMode.Data)]
    [InlineData("!!", AssistantMode.Data)]
    [InlineData("!exit", AssistantMode.Estate)]
    [InlineData("  !EXIT which flow loads the sales table?", AssistantMode.Estate)]
    [InlineData("! exit", AssistantMode.Estate)]
    [InlineData("!exiting stock by warehouse", AssistantMode.Data)]
    [InlineData("!exits per region", AssistantMode.Data)]
    [InlineData("what is our revenue by region", null)]
    [InlineData("why did the run fail!", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void AMessagesCommand_IsReadFromItsFirstCharacters(string? message, AssistantMode? expected)
    {
        Assert.Equal(expected, AssistantQuestions.CommandOf(message));
    }

    [Fact]
    public void AConversationStaysInTheModeItsNewestCommandSet()
    {
        ConversationTurn[] data =
        [
            new(false, "!how many customers do we have"),
            new(true, "You have 1,204 customers."),
        ];
        // A follow-up keeps data mode without repeating the command.
        Assert.Equal(AssistantMode.Data, AssistantQuestions.ModeOf("and by region?", data));
        Assert.Equal(AssistantMode.Data, AssistantQuestions.ModeOf("run it", data));
        // `!exit` returns to estate mode, alone or with a question after it.
        Assert.Equal(AssistantMode.Estate, AssistantQuestions.ModeOf("!exit which flow loads that table?", data));

        ConversationTurn[] exited =
        [
            .. data,
            new(false, "!exit"),
            new(true, "Back to questions about the estate."),
        ];
        Assert.Equal(AssistantMode.Estate, AssistantQuestions.ModeOf("which flow loads the sales table?", exited));
        Assert.Equal(AssistantMode.Data, AssistantQuestions.ModeOf("!and the orders?", exited));

        // A conversation that never carried a command is about the estate.
        Assert.Equal(AssistantMode.Estate, AssistantQuestions.ModeOf("why did the load fail?", []));
    }

    [Fact]
    public void OnlyThePersonsOwnMessages_MoveAConversation()
    {
        // An answer can quote a command back ("ask again as !what is our revenue"); that is not the person asking.
        ConversationTurn[] estate =
        [
            new(false, "what is our revenue by region?"),
            new(true, "!what is our revenue by region answers that from the data."),
        ];
        Assert.Equal(AssistantMode.Estate, AssistantQuestions.ModeOf("which flow loads the sales table?", estate));
    }

    [Fact]
    public async Task TheRoute_OpensTheSessionInTheConversationsMode_WithTheServersInstructions()
    {
        foreach (var surface in new[] { AssistantSurface.Slack, AssistantSurface.Gui })
        {
            var settings = Settings(surface);
            using var instructions = Instructions(settings, "SERVER RULES: modes, routing, tools.");
            var router = new AssistantRouter(settings, instructions);

            var data = await router.ForAsync(Request("and by region?", [new(false, "!how many customers"), new(true, "1,204.")]), default);
            Assert.Equal(AssistantMode.Data, data.Mode);
            Assert.Equal("https://mcp.example/mcp?surface=assistant&mode=data", data.ServerUri.AbsoluteUri);
            Assert.StartsWith("SERVER RULES: modes, routing, tools.\n\n", data.Instructions, StringComparison.Ordinal);
            Assert.Contains(AssistantInstructions.Surface(settings), data.Instructions, StringComparison.Ordinal);
            // No deployment narrowing: every tool the session lists in its mode.
            Assert.Empty(data.Tools);

            var estate = await router.ForAsync(Request("why did the load fail?", []), default);
            Assert.Equal(AssistantMode.Estate, estate.Mode);
            Assert.Equal("https://mcp.example/mcp?surface=assistant&mode=estate", estate.ServerUri.AbsoluteUri);
        }
    }

    [Fact]
    public async Task ADeploymentsNarrowing_IsPassedOnAsGiven_OnceEach()
    {
        var settings = Settings(AssistantSurface.Gui);
        settings.Mcp.AllowedTools = ["summary", "list_runs", "summary", " "];
        using var instructions = Instructions(settings, "SERVER RULES.");

        var route = await new AssistantRouter(settings, instructions).InAsync(AssistantMode.Estate, "bearer", default);

        Assert.Equal(["summary", "list_runs"], route.Tools);
    }

    internal static AssistantSettings Settings(AssistantSurface surface)
        => new() { Surface = surface, GuiBaseUrl = string.Empty, Mcp = new McpOptions { ServerUrl = "https://mcp.example/mcp" } };

    internal static AssistantRequest Request(string question, IReadOnlyList<ConversationTurn> prior)
        => new() { ConversationKey = "c-1", PriorTurns = prior, Question = question, McpBearer = "delegated" };

    /// <summary>A reader whose MCP server answers initialize with <paramref name="text"/> as plain JSON.</summary>
    internal static McpServerInstructions Instructions(AssistantSettings settings, string text)
        => new(settings, NullLogger<McpServerInstructions>.Instance, TimeProvider.System, new McpServerStub(
            _ => McpServerStub.Json(
                """{"jsonrpc":"2.0","id":1,"result":{"instructions":""" + System.Text.Json.JsonSerializer.Serialize(text) + "}}")));
}

/// <summary>An MCP server in memory: answers each request with what the test gives it, and keeps what it was sent.</summary>
internal sealed class McpServerStub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
{
    public List<(HttpMethod Method, Uri Uri, string? Authorization, string? Session, string? Body)> Received { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Received.Add((
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            request.Headers.TryGetValues("Mcp-Session-Id", out var ids) ? ids.First() : null,
            body));
        return answer(request);
    }

    public static HttpResponseMessage Json(string body, string? session = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (session is not null)
        {
            response.Headers.Add("Mcp-Session-Id", session);
        }
        return response;
    }

    public static HttpResponseMessage Events(string body, string session)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        };
        response.Headers.Add("Mcp-Session-Id", session);
        return response;
    }
}
