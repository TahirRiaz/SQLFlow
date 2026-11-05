using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SqlFlow.Assistant;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>What the chat suites share: asking the control plane's chat a question, and a provider that answers with
/// the events a test queued.</summary>
internal static class ChatTesting
{
    internal static async Task<(Guid ConversationId, string Body)> AskAsync(
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
    internal sealed class ScriptedGateway : IAssistantGateway
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
