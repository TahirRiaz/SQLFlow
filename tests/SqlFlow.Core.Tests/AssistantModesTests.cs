using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
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
    [InlineData("!data total sales", AssistantMode.Data)]
    [InlineData("!DATA", AssistantMode.Data)]
    [InlineData("!!", AssistantMode.Data)]
    [InlineData("!exit", AssistantMode.Estate)]
    [InlineData("  !EXIT which flow loads the sales table?", AssistantMode.Estate)]
    [InlineData("! exit", AssistantMode.Estate)]
    [InlineData("!exit.", AssistantMode.Estate)]
    [InlineData("!estate which schedules fire tonight?", AssistantMode.Estate)]
    [InlineData("!exiting stock by warehouse", AssistantMode.Data)]
    [InlineData("!exits per region", AssistantMode.Data)]
    [InlineData("!exit-interview counts", AssistantMode.Data)]
    [InlineData("what is our revenue by region", null)]
    [InlineData("why did the run fail!", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void AMessagesCommand_IsReadFromItsFirstCharacters(string? message, AssistantMode? expected)
    {
        Assert.Equal(expected, AssistantQuestions.CommandOf(message));
    }

    /// <summary>One table holds every reader to one grammar: the MCP server reads it for an editor's model, this reader
    /// for the GUI chat and the Slack bot, and the GUI for the questions it saves.</summary>
    [Fact]
    public void EveryMessageInTheSharedTable_IsReadAsTheTableSays()
    {
        using var table = JsonDocument.Parse(File.ReadAllText(SharedTablePath()));
        var cases = table.RootElement.GetProperty("cases").EnumerateArray().ToList();
        Assert.True(cases.Count >= 50, $"the table covers the grammar, {cases.Count} cases");
        foreach (var entry in cases)
        {
            var message = entry.GetProperty("message").GetString()!;
            AssistantMode? mode = entry.GetProperty("mode").GetString() switch
            {
                "data" => AssistantMode.Data,
                "estate" => AssistantMode.Estate,
                null => null,
                var other => throw new InvalidOperationException($"The table names an unknown mode '{other}'."),
            };
            var read = AssistantQuestions.Read(message);
            Assert.True(mode == read?.Mode, $"{Shown(message)}: expected {mode?.ToString() ?? "no command"}, read {read?.Mode.ToString() ?? "no command"}");
            Assert.True(entry.GetProperty("question").GetString() == read?.Question,
                $"{Shown(message)}: expected question {Shown(entry.GetProperty("question").GetString())}, read {Shown(read?.Question)}");
        }
    }

    /// <summary>White space here is Unicode's White_Space, the set the MCP server's reader skips: the two must agree on
    /// every character, or a message would be read one way in the GUI and another in an editor.</summary>
    [Fact]
    public void OnlyWhiteSpaceAndInvisibleCharacters_MayComeBeforeTheCommand()
    {
        int[] whiteSpace =
        [
            0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x20, 0x85, 0xA0, 0x1680, 0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005,
            0x2006, 0x2007, 0x2008, 0x2009, 0x200A, 0x2028, 0x2029, 0x202F, 0x205F, 0x3000,
        ];
        int[] invisible = [0x180E, 0x200B, 0x200C, 0x200D, 0x2060, 0xFEFF];
        for (var code = 0; code <= 0xFFFF; code++)
        {
            var c = (char)code;
            Assert.True(char.IsWhiteSpace(c) == whiteSpace.Contains(code), $"U+{code:X4}: .NET and Unicode White_Space disagree");
            var read = AssistantQuestions.Read(c + "!exit");
            if (whiteSpace.Contains(code) || invisible.Contains(code) || c == AssistantQuestions.Command)
            {
                Assert.True(read == new AssistantCommand(AssistantMode.Estate, string.Empty), $"U+{code:X4} read {read}");
            }
            else
            {
                Assert.True(read is null, $"U+{code:X4} read {read}");
            }
        }
    }

    /// <summary>A command word counts only ending the message or followed by white space, an invisible character or
    /// sentence punctuation; anything else makes it a word of the question.</summary>
    [Fact]
    public void ACommandWord_EndsOnlyAtWhiteSpaceOrSentencePunctuation()
    {
        const string Ends = "\u180E\u200B\u200C\u200D\u2060\uFEFF.,:;!?";
        foreach (var (word, mode) in new[] { ("data", AssistantMode.Data), ("exit", AssistantMode.Estate), ("estate", AssistantMode.Estate) })
        {
            for (var code = 0; code <= 0xFFFF; code++)
            {
                var c = (char)code;
                var message = $"!{word}{c}x";
                var read = AssistantQuestions.Read(message);
                var ends = char.IsWhiteSpace(c) || Ends.Contains(c, StringComparison.Ordinal);
                var expected = ends ? new AssistantCommand(mode, "x") : new AssistantCommand(AssistantMode.Data, $"{word}{c}x");
                Assert.True(read == expected, $"{Shown(message)} read {read}");
            }
        }
    }

    [Theory]
    [InlineData("!ex\u0131t")]
    [InlineData("!EX\u0130T")]
    [InlineData("!estate\u212A")]
    [InlineData("!dat\u0430 x")]
    [InlineData("!\uFF45\uFF58\uFF49\uFF54")]
    public void ALookalikeOfACommandWord_IsAQuestionForTheData(string message)
    {
        Assert.Equal(AssistantMode.Data, AssistantQuestions.CommandOf(message));
        Assert.Equal(message[1..], AssistantQuestions.QuestionOf(message));
    }

    /// <summary>The shared table, found from the test binary (the solution above it) or, for a build whose output lives
    /// elsewhere, from this source file.</summary>
    private static string SharedTablePath([CallerFilePath] string source = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(source) ?? string.Empty })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var table = Path.Combine(directory.FullName, "tools", "sqlflow-mcp", "tests", "assistant-commands.json");
                if (File.Exists(Path.Combine(directory.FullName, "SqlFlow.sln")) && File.Exists(table))
                {
                    return table;
                }
            }
        }
        throw new InvalidOperationException(
            $"Could not find tools/sqlflow-mcp/tests/assistant-commands.json beside SqlFlow.sln above '{AppContext.BaseDirectory}' or '{source}'.");
    }

    /// <summary>A message with its invisible and control characters spelled out, for a failure that names it.</summary>
    private static string Shown(string? text)
        => text is null ? "null" : "\"" + string.Concat(text.Select(c => c < 0x20 || c > 0x7E ? $"\\u{(int)c:X4}" : c.ToString())) + "\"";

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
    public void TheModeAnAnswerWasRecordedIn_CarriesTheConversation()
    {
        // The command that set data mode is older than the turns a host replays; the newest answer's recorded mode
        // still holds the conversation in it.
        ConversationTurn[] replayed =
        [
            new(false, "and by region?"),
            new(true, "Norway holds 412 orders.", AssistantMode.Data),
        ];
        Assert.Equal(AssistantMode.Data, AssistantQuestions.ModeOf("and by country?", replayed));

        // Asking for the data in words is not the command: an answer given in estate mode keeps the conversation there.
        ConversationTurn[] inWords =
        [
            new(false, "switch to data mode please"),
            new(true, "Start the message with !data to answer it from the data.", AssistantMode.Estate),
        ];
        Assert.Equal(AssistantMode.Estate, AssistantQuestions.ModeOf("how many customers do we have?", inWords));

        // A newer command of the person's wins over an older answer's mode, and a message's own command over both.
        Assert.Equal(AssistantMode.Data, AssistantQuestions.ModeOf("and by region?", [.. inWords, new(false, "!data revenue")]));
        Assert.Equal(AssistantMode.Data, AssistantQuestions.ModeOf("!data revenue by region", inWords));
        Assert.Equal(AssistantMode.Estate, AssistantQuestions.ModeOf("!exit", replayed));
        Assert.Equal(AssistantMode.Estate, AssistantQuestions.ModeOf("!estate which flows failed?", replayed));

        // An answer recorded without a mode (an error reply, or one saved before modes were recorded) says nothing:
        // the turns before it decide.
        ConversationTurn[] unrecorded =
        [
            new(false, "!how many customers do we have"),
            new(true, "Sorry, something went wrong."),
        ];
        Assert.Equal(AssistantMode.Data, AssistantQuestions.ModeOf("try again", unrecorded));
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
