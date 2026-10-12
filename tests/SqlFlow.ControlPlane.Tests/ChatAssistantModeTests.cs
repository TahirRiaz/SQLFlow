using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Assistant;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Security;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The GUI chat remembers the mode each answer ended in: the control plane stores it with the answer, and the next
/// question's earlier turns carry it, so a conversation the assistant switched (the person asked in words, or a
/// follow-up turned to the estate) starts its next message in the mode the switch left it in. A stopped or failed
/// answer keeps the mode its run last restarted in.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ChatAssistantModeTests
{
    [SkippableFact]
    public async Task TheModeAnAnswerEndedIn_IsStored_AndStartsTheNextQuestion()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var gateway = new ScriptedGateway();
        // A configured assistant whose provider is never reached: the scripted gateway replaces the one the host
        // registers, and the key is a reference nothing resolves.
        await using var factory = new ControlPlaneAppFactory()
            .WithCatalog(cs)
            .WithSetting("ControlPlane:Assistant:Enabled", "true")
            .WithSetting("ControlPlane:Assistant:Provider", "Anthropic")
            .WithSetting("ControlPlane:Assistant:Mcp:ServerUrl", "http://127.0.0.1:1/mcp")
            .WithSetting("ControlPlane:Assistant:Anthropic:ApiKey", "${env:SQLFLOW_TEST_UNUSED_ANTHROPIC_KEY}")
            .WithSetting("ControlPlane:Assistant:Anthropic:Model", "scripted")
            .WithServices(services => services.AddSingleton<IAssistantGateway>(gateway));
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var nowUtc = DateTime.UtcNow;
        var token = factory.Services.GetRequiredService<TokenIssuer>()
            .Issue("chat-mode-user", ["read", "operate"], nowUtc, "Operator", userId, nowUtc).Token;
        await using var db = CatalogDatabase.Create(cs);

        try
        {
            // The person asks for data mode in words; the run switches and the answer ends in data mode.
            gateway.Next(
                new AssistantEvent.ToolCall(AssistantRuns.StartDataModeTool, AssistantToolCallStatus.Started),
                new AssistantEvent.Restarted(AssistantMode.Data),
                new AssistantEvent.TextDelta("Data mode is on."),
                new AssistantEvent.Completed("Data mode is on.") { Mode = AssistantMode.Data });
            var first = await AskAsync(client, token, null, "switch to data mode please");
            Assert.Contains("event: done", first.Body, StringComparison.Ordinal);

            // The follow-up carries no command, and starts in data mode all the same.
            gateway.Next(
                new AssistantEvent.TextDelta("1,204."),
                new AssistantEvent.Completed("1,204.") { Mode = AssistantMode.Data });
            await AskAsync(client, token, first.ConversationId, "how many customers do we have?");
            var asked = gateway.Requests[^1];
            Assert.Equal(
                [new ConversationTurn(false, "switch to data mode please"), new ConversationTurn(true, "Data mode is on.", AssistantMode.Data)],
                asked.PriorTurns);
            Assert.Equal(AssistantMode.Data, AssistantQuestions.ModeOf(asked.Question, asked.PriorTurns));

            // An answer that fails part way, after its run restarted in estate mode, keeps that mode with its text.
            gateway.Next(
                new AssistantEvent.ToolCall(AssistantRuns.EndDataModeTool, AssistantToolCallStatus.Started),
                new AssistantEvent.Restarted(AssistantMode.Estate),
                new AssistantEvent.TextDelta("The customers flow loads"));
            var failed = await AskAsync(client, token, first.ConversationId, "which flow loads that table?");
            Assert.Contains("event: error", failed.Body, StringComparison.Ordinal);

            var stored = await db.ChatMessages.AsNoTracking()
                .Where(m => m.ConversationId == first.ConversationId)
                .OrderBy(m => m.Ordinal)
                .Select(m => new { m.Role, m.Text, m.AssistantMode })
                .ToListAsync();
            Assert.Equal(
                [
                    (ChatMessageRoles.User, (string?)null),
                    (ChatMessageRoles.Assistant, "data"),
                    (ChatMessageRoles.User, null),
                    (ChatMessageRoles.Assistant, "data"),
                    (ChatMessageRoles.User, null),
                    (ChatMessageRoles.Assistant, "estate"),
                ],
                stored.Select(m => (m.Role, m.AssistantMode)));
            Assert.Equal("The customers flow loads", stored[^1].Text);

            gateway.Next(
                new AssistantEvent.TextDelta("Nightly."),
                new AssistantEvent.Completed("Nightly.") { Mode = AssistantMode.Estate });
            await AskAsync(client, token, first.ConversationId, "and when does it run?");
            asked = gateway.Requests[^1];
            Assert.Equal(AssistantMode.Estate, AssistantQuestions.ModeOf(asked.Question, asked.PriorTurns));
        }
        finally
        {
            var conversations = db.ChatConversations.Where(c => c.UserId == userId).Select(c => c.Id);
            await db.ChatMessages.Where(m => conversations.Contains(m.ConversationId)).ExecuteDeleteAsync();
            await db.ChatConversations.Where(c => c.UserId == userId).ExecuteDeleteAsync();
        }
    }

    private static async Task<(Guid ConversationId, string Body)> AskAsync(
        HttpClient client, string token, Guid? conversationId, string question)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/chat/ask", UriKind.Relative))
        {
            Content = JsonContent.Create(new ChatAskRequest(conversationId, question, null)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        const string Opening = "event: conversation\ndata: ";
        var start = body.IndexOf(Opening, StringComparison.Ordinal);
        Assert.True(start >= 0, body);
        var data = body[(start + Opening.Length)..body.IndexOf('\n', start + Opening.Length)];
        using var conversation = JsonDocument.Parse(data);
        var id = conversation.RootElement.GetProperty("id").GetGuid();
        Assert.True(conversationId is null || conversationId == id, body);
        return (id, body);
    }

    /// <summary>Answers each question with the events the test queued for it; a script that ends without a completed
    /// answer fails the run, as a provider's error would.</summary>
    private sealed class ScriptedGateway : IAssistantGateway
    {
        private readonly Queue<AssistantEvent[]> _scripts = new();

        public List<AssistantRequest> Requests { get; } = [];

        public void Next(params AssistantEvent[] events) => _scripts.Enqueue(events);

        public Task<AssistantAnswer> AskAsync(AssistantRequest request, CancellationToken ct)
            => AssistantRuns.AskAsync(StreamAsync(request, ct), request, new AssistantSettings(), "Scripted", ct);

        public async IAsyncEnumerable<AssistantEvent> StreamAsync(
            AssistantRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Requests.Add(request);
            var script = _scripts.Dequeue();
            foreach (var evt in script)
            {
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                yield return evt;
            }
            if (script[^1] is not AssistantEvent.Completed)
            {
                throw new InvalidOperationException("The scripted provider failed part way.");
            }
        }
    }
}
