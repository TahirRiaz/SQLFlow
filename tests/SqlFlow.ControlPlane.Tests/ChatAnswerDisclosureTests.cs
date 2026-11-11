using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Assistant;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Security;
using Xunit;
using static SqlFlow.ControlPlane.Tests.ChatTesting;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The chat tells a person, before their first question, that an AI language model writes its answers, which one, and
/// that they can be wrong; and every answer it stores, a failed partial one included, records the model that wrote it,
/// which the stream and the transcript serve with the answer. A person's message records no model, and neither does an
/// answer stored before answers recorded theirs.
/// </summary>
public sealed class ChatAnswerDisclosureTests
{
    /// <summary>The variable the test's key reference names: this suite's alone, so setting it touches no other test.</summary>
    private const string UnusedKeyVariable = "SQLFLOW_TEST_CHAT_DISCLOSURE_UNUSED_KEY";

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task TheChatSaysAModelWritesItsAnswers_AndEveryAnswerRecordsTheModelThatWroteIt()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var gateway = new ScriptedGateway();
        // A configured assistant whose provider is never reached: the scripted gateway replaces the one the host
        // registers. The capabilities endpoint resolves the assistant's settings, keys included, so the key is a
        // reference that resolves, to a value no provider would take.
        Environment.SetEnvironmentVariable(UnusedKeyVariable, "not-a-key");
        await using var factory = new ControlPlaneAppFactory()
            .WithCatalog(cs)
            .WithSetting("ControlPlane:Assistant:Enabled", "true")
            .WithSetting("ControlPlane:Assistant:Provider", "Anthropic")
            .WithSetting("ControlPlane:Assistant:Mcp:ServerUrl", "http://127.0.0.1:1/mcp")
            .WithSetting("ControlPlane:Assistant:Anthropic:ApiKey", $"${{env:{UnusedKeyVariable}}}")
            .WithSetting("ControlPlane:Assistant:Anthropic:Model", "claude-test-model")
            .WithServices(services => services.AddSingleton<IAssistantGateway>(gateway));
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var nowUtc = DateTime.UtcNow;
        var token = factory.Services.GetRequiredService<TokenIssuer>()
            .Issue("chat-disclosure-user", ["read", "operate"], nowUtc, "Operator", userId, nowUtc).Token;
        await using var db = CatalogDatabase.Create(cs);

        try
        {
            var capabilities = await GetAsync<ChatCapabilitiesDto>(client, token, "/api/v1/chat/capabilities");
            Assert.Equal("Anthropic", capabilities.Provider);
            Assert.Equal("claude-test-model", capabilities.Model);
            Assert.Equal(
                "Answers are written by an AI language model (claude-test-model on Anthropic) and can be wrong: "
                + "check what matters before you act on it.",
                capabilities.Disclosure);

            gateway.Next(
                new AssistantEvent.TextDelta("Three flows failed."),
                new AssistantEvent.Completed("Three flows failed."));
            var first = await AskAsync(client, token, null, "which flows failed last night?");
            Assert.Contains("\"label\":\"claude-test-model on Anthropic\"", first.Body, StringComparison.Ordinal);

            // An answer that fails part way is stored with what it said, and marked all the same.
            gateway.Next(new AssistantEvent.TextDelta("The first of them"));
            var failed = await AskAsync(client, token, first.ConversationId, "why?");
            Assert.Contains("event: error", failed.Body, StringComparison.Ordinal);

            var messages = await GetAsync<List<ChatMessageDto>>(
                client, token, $"/api/v1/chat/conversations/{first.ConversationId}/messages");
            var answerModel = new ChatModelDto("Anthropic", "claude-test-model", "claude-test-model on Anthropic");
            Assert.Equal(
                [
                    (ChatMessageRoles.User, (ChatModelDto?)null),
                    (ChatMessageRoles.Assistant, answerModel),
                    (ChatMessageRoles.User, null),
                    (ChatMessageRoles.Assistant, answerModel),
                ],
                messages.Select(m => (m.Role, m.Model)));
            Assert.Equal("The first of them", messages[3].Text);

            var stored = await db.ChatMessages.AsNoTracking()
                .Where(m => m.ConversationId == first.ConversationId && m.Role == ChatMessageRoles.Assistant)
                .Select(m => new { m.ModelProvider, m.Model })
                .ToListAsync();
            Assert.All(stored, m => Assert.Equal(("Anthropic", "claude-test-model"), (m.ModelProvider, m.Model)));
        }
        finally
        {
            var conversations = db.ChatConversations.Where(c => c.UserId == userId).Select(c => c.Id);
            await db.ChatMessages.Where(m => conversations.Contains(m.ConversationId)).ExecuteDeleteAsync();
            await db.ChatConversations.Where(c => c.UserId == userId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task WithTheAssistantOff_TheChatNamesNoModel_AndDisclosesNothing()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        using var client = factory.CreateClient();
        var nowUtc = DateTime.UtcNow;
        var token = factory.Services.GetRequiredService<TokenIssuer>()
            .Issue("chat-disclosure-off", ["read"], nowUtc, "Reader", Guid.NewGuid(), nowUtc).Token;

        var capabilities = await GetAsync<ChatCapabilitiesDto>(client, token, "/api/v1/chat/capabilities");

        Assert.False(capabilities.Enabled);
        Assert.Equal("", capabilities.Model);
        Assert.Equal("", capabilities.Disclosure);
    }

    [Fact]
    public void AStoredAnswer_ServesItsModel_AndAMessageThatRecordsNoneServesNothing()
    {
        CatalogChatMessage Answer(string? provider, string? model) => new()
        {
            Role = ChatMessageRoles.Assistant, Text = "An answer.", ModelProvider = provider, Model = model,
        };

        Assert.Equal(
            new ChatModelDto("AzureFoundry", "gpt-5.1", "gpt-5.1 on Azure AI Foundry"),
            ChatEndpoints.ModelOf(Answer("AzureFoundry", "gpt-5.1")));
        // A provider this build does not know is labelled by the model alone, rather than dropped.
        Assert.Equal(
            new ChatModelDto("SomeFutureProvider", "model-x", "model-x"),
            ChatEndpoints.ModelOf(Answer("SomeFutureProvider", "model-x")));
        Assert.Null(ChatEndpoints.ModelOf(Answer(null, null)));
        Assert.Null(ChatEndpoints.ModelOf(Answer("OpenAI", " ")));
        Assert.Null(ChatEndpoints.ModelOf(new CatalogChatMessage
        {
            Role = ChatMessageRoles.User, Text = "A question.", ModelProvider = "OpenAI", Model = "gpt-5-mini",
        }));
    }

    [Theory]
    [InlineData(AssistantProvider.AzureFoundry, "gpt-5.1 on Azure AI Foundry")]
    [InlineData(AssistantProvider.OpenAI, "gpt-5-mini on OpenAI")]
    [InlineData(AssistantProvider.Anthropic, "claude-opus-4-8 on Anthropic")]
    public void EachProvider_NamesTheModelItsSettingsName(AssistantProvider provider, string label)
    {
        var settings = new AssistantSettings
        {
            Provider = provider,
            Foundry = new FoundryOptions { ModelDeploymentName = " gpt-5.1 " },
        };

        Assert.Equal(label, AssistantDisclosure.ModelLabel(provider, settings.Model));
        Assert.Equal($"AI-generated by {label}; it can be wrong.", AssistantDisclosure.Mark(provider, settings.Model));
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"{path} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<T>()
            ?? throw new InvalidOperationException($"{path} answered no body.");
    }
}
