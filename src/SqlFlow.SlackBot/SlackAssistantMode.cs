using Newtonsoft.Json.Linq;
using SlackNet;
using SqlFlow.Assistant;

namespace SqlFlow.SlackBot;

/// <summary>
/// The mode an answer was given in, and the model that wrote it, carried on the bot's reply as Slack message metadata. The
/// mode makes a thread its own record of the mode: the next question in it starts from the mode the last answer was in,
/// the same as in the GUI chat, where the control plane stores it with the message. The model marks the reply as
/// generated in a form a program reads (<c>ai_generated</c>, <c>provider</c>, <c>model</c>), as the note the reply ends
/// with marks it for a person. Metadata is not shown in the thread.
/// </summary>
internal static class SlackAssistantMode
{
    /// <summary>The metadata event type the bot's replies carry.</summary>
    public const string EventType = "sqlflow_assistant_mode";

    /// <summary>The metadata for a reply given in <paramref name="mode"/> and written by <paramref name="model"/> of
    /// <paramref name="provider"/>.</summary>
    public static MessageMetadata For(AssistantMode mode, AssistantProvider provider, string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        return new()
        {
            EventType = EventType,
            EventPayload = new JObject
            {
                ["mode"] = mode == AssistantMode.Data ? "data" : "estate",
                ["ai_generated"] = true,
                ["provider"] = provider.ToString(),
                ["model"] = model.Trim(),
            },
        };
    }

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
