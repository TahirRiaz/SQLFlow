using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlFlow.ControlPlane.Notifications;

/// <summary>One persisted flow group of a digest: the shape the GUI's digest table renders row by row.</summary>
/// <remarks>
/// Stored rather than recomputed, because a digest outlives the events behind it: the event retention prunes at
/// 30 days while a digest is kept for a year, and a digest whose table went blank the moment its events aged out
/// would be a record of nothing. The error is stored as the same bounded excerpt the message bodies show, so one
/// enormous engine error cannot bloat the row.
/// </remarks>
public sealed record NotificationDigestGroup(
    [property: JsonPropertyName("flow")] string FlowName,
    [property: JsonPropertyName("kind")] string FlowKind,
    [property: JsonPropertyName("event")] string EventKind,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("last")] DateTime LastOccurredUtc,
    [property: JsonPropertyName("run")] Guid LastRunId,
    [property: JsonPropertyName("pipeline")] Guid PipelineId,
    [property: JsonPropertyName("error")] string? LastError);

/// <summary>
/// The one definition of how a digest's flow groups are stored and read back. Both ends live here so the written
/// and parsed shapes cannot drift apart, and both sides agree on the cap: a pathological window can produce
/// thousands of groups, and the column is read on every digest list expansion.
/// </summary>
public static class NotificationDigestGroups
{
    /// <summary>
    /// The most groups one digest persists. The groups are already ordered failures first and most recent first,
    /// so a cap keeps the rows that matter. Anything past it is still counted in the digest's totals, which is
    /// what lets a reader see that the table is showing part of a larger window.
    /// </summary>
    public const int MaxGroups = 200;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Renders the groups for storage, bounded in both row count and error length.</summary>
    public static string Serialize(IReadOnlyList<NotificationFlowGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var stored = groups
            .Take(MaxGroups)
            .Select(g => new NotificationDigestGroup(
                g.FlowName, g.FlowKind, g.Kind, g.Count, g.LastOccurredUtc, g.LastRunId, g.PipelineId,
                string.IsNullOrWhiteSpace(g.LastError) ? null : NotificationComposer.ErrorExcerpt(g.LastError)))
            .ToList();
        return JsonSerializer.Serialize(stored, Options);
    }

    /// <summary>Reads a stored group list back. A row written before the column existed, or one whose text is not
    /// a group array, reads as no groups: the digest's own bodies and counts still tell the reader everything.</summary>
    public static IReadOnlyList<NotificationDigestGroup> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<NotificationDigestGroup>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
