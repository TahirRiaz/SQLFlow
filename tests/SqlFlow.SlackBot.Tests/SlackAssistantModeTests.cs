using Newtonsoft.Json.Linq;
using SlackNet;
using SqlFlow.Assistant;
using SqlFlow.SlackBot;
using Xunit;

namespace SqlFlow.SlackBot.Tests;

/// <summary>
/// A Slack thread remembers the mode each of the bot's answers ended in through the reply's message metadata, so the
/// next question in the thread starts there; a reply without it (an error, an answer from before modes were recorded,
/// or another app's metadata) says nothing, and the earlier turns decide.
/// </summary>
public class SlackAssistantModeTests
{
    [Theory]
    [InlineData(AssistantMode.Data, "data")]
    [InlineData(AssistantMode.Estate, "estate")]
    public void AReplysMetadata_RecordsTheModeItsAnswerEndedIn(AssistantMode mode, string stored)
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
