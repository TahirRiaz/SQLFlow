using Newtonsoft.Json.Linq;
using SlackNet;
using SqlFlow.Assistant;
using SqlFlow.SlackBot;
using Xunit;

namespace SqlFlow.SlackBot.Tests;

/// <summary>
/// A Slack thread remembers the mode each of the bot's answers was given in through the reply's message metadata, so the
/// next question in the thread starts there; a reply without it (an error, an answer from before modes were recorded,
/// or another app's metadata) says nothing, and the earlier turns decide. A question's command is read after the
/// mentions in it are taken out, so one asked in a channel, which opens with the bot's mention, is routed as typed.
/// </summary>
public class SlackAssistantModeTests
{
    [Theory]
    [InlineData("<@U0BOT> !data which regions hold open orders?", "!data which regions hold open orders?", AssistantMode.Data)]
    [InlineData("<@U0BOT|sqlflow> !exit", "!exit", AssistantMode.Estate)]
    [InlineData("<@W0BOT>!data orders per region", "!data orders per region", AssistantMode.Data)]
    [InlineData("  <@U0BOT>   ! how many customers", "! how many customers", AssistantMode.Data)]
    [InlineData("<@U0BOT> <@U0BOT|sqlflow> !estate", "!estate", AssistantMode.Estate)]
    [InlineData("<@U0BOT> which flow failed last night?", "which flow failed last night?", null)]
    [InlineData("which flow did <@U456|ann> fix? !data", "which flow did  fix? !data", null)]
    [InlineData("<@u0bot> !data", "<@u0bot> !data", null)]
    [InlineData("", "", null)]
    [InlineData(null, "", null)]
    public void AQuestionsCommand_IsReadAfterItsMentionsAreTakenOut(string? text, string question, AssistantMode? mode)
    {
        var read = SlackAssistantHandler.WithoutMentions(text);

        Assert.Equal(question, read);
        Assert.Equal(mode, AssistantQuestions.CommandOf(read));
    }

    [Theory]
    [InlineData(AssistantMode.Data, "data")]
    [InlineData(AssistantMode.Estate, "estate")]
    public void AReplysMetadata_RecordsTheModeItsAnswerWasGivenIn(AssistantMode mode, string stored)
    {
        var metadata = SlackAssistantMode.For(mode);

        Assert.Equal(SlackAssistantMode.EventType, metadata.EventType);
        Assert.Equal(stored, metadata.EventPayload["mode"]?.Value<string>());
        Assert.Equal(mode, SlackAssistantMode.Of(metadata));
    }

    [Fact]
    public void MetadataThatRecordsNoMode_SaysNothing()
    {
        Assert.Null(SlackAssistantMode.Of(null));
        Assert.Null(SlackAssistantMode.Of(new MessageMetadata { EventType = "another_app", EventPayload = new JObject { ["mode"] = "data" } }));
        Assert.Null(SlackAssistantMode.Of(new MessageMetadata { EventType = SlackAssistantMode.EventType, EventPayload = new JObject() }));
        Assert.Null(SlackAssistantMode.Of(new MessageMetadata { EventType = SlackAssistantMode.EventType, EventPayload = new JObject { ["mode"] = "sideways" } }));
        Assert.Null(SlackAssistantMode.Of(new MessageMetadata { EventType = SlackAssistantMode.EventType, EventPayload = new JArray() }));
    }
}
