using Newtonsoft.Json.Linq;
using SlackNet;
using SqlFlow.Assistant;

namespace SqlFlow.SlackBot;

/// <summary>
/// The mode an answer was given in, carried on the bot's reply as Slack message metadata, so a thread is its own record of
/// the mode: the next question in it starts from the mode the last answer was in, the same as in the GUI chat, where
/// the control plane stores it with the message. Metadata is not shown in the thread and holds only the mode's name.
/// </summary>
internal static class SlackAssistantMode
{
    /// <summary>The metadata event type the bot's replies carry.</summary>
    public const string EventType = "sqlflow_assistant_mode";

    /// <summary>The metadata for a reply given in <paramref name="mode"/>.</summary>
    public static MessageMetadata For(AssistantMode mode) => new()
    {
        EventType = EventType,
        EventPayload = new JObject { ["mode"] = mode == AssistantMode.Data ? "data" : "estate" },
    };

    /// <summary>The mode a reply's <paramref name="metadata"/> records, or null when it records none: a reply posted
    /// without it (an error, or an answer from before modes were recorded), or metadata another app put there.</summary>
    public static AssistantMode? Of(MessageMetadata? metadata)
    {
        if (metadata is not { EventType: EventType, EventPayload: JObject payload })
        {
            return null;
        }
        return payload.Value<string>("mode") switch
        {
            "data" => AssistantMode.Data,
            "estate" => AssistantMode.Estate,
            _ => null,
        };
    }
}
