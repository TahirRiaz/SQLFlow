using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Notifications;

/// <summary>
/// Produces an estate digest: one composed, persisted summary of every notification event in a window. This is
/// the single path both producers go through, so an on-demand digest generated from the GUI is byte-for-byte the
/// same artifact the background service writes on its own schedule, and neither depends on a subscription
/// existing or a channel being configured.
/// </summary>
/// <remarks>
/// The two producers differ only in how their window is bounded. A scheduled digest chains on the generator's
/// event-id cursor with no lower time bound, so an event detected late (a node whose clock trailed, a run row
/// written moments after the boundary) still lands in the next digest instead of falling between two windows. A
/// manual digest is a report over a period the caller named: it reads by time and never touches the cursor, so
/// asking for one can never rob the next scheduled digest of its events.
/// </remarks>
public static class NotificationDigestGenerator
{
    /// <summary>
    /// The most events one digest accounts for. Far above the composer's own rendering cap (which lists 15 flow
    /// groups): the extra headroom is what keeps the per-kind counts honest on a bad day, and what lets a backlog
    /// drain in one window rather than 500 events per period. Anything beyond it is carried into the next digest.
    /// </summary>
    public const int MaxEventsPerDigest = 5000;

    /// <summary>The control plane's own periodic digest: everything after the generator's cursor, detected up to
    /// this instant. Advances the cursor over exactly what it covered.</summary>
    public static Task<CatalogNotificationDigest> GenerateScheduledAsync(
        CatalogDbContext catalog, long afterEventId, DateTime periodStartUtc, DateTime nowUtc, string? guiBaseUrl,
        CancellationToken ct = default)
        => GenerateAsync(
            catalog, NotificationDigestOrigins.Scheduled, afterEventId, fromUtc: null, periodStartUtc,
            periodEndUtc: nowUtc, generatedUtc: nowUtc, generatedByUserId: null, advanceCursor: true, guiBaseUrl, ct);

    /// <summary>
    /// A digest a person asked for over a period they chose, which is very often a past day rather than a window
    /// ending now: the period the digest reports on and the instant it was produced at are therefore separate.
    /// A report only, so the scheduled cursor is left alone and the next scheduled digest still covers everything
    /// it was going to.
    /// </summary>
    public static Task<CatalogNotificationDigest> GenerateManualAsync(
        CatalogDbContext catalog, DateTime periodStartUtc, DateTime periodEndUtc, DateTime generatedUtc,
        Guid generatedByUserId, string? guiBaseUrl, CancellationToken ct = default)
        => GenerateAsync(
            catalog, NotificationDigestOrigins.Manual, afterEventId: 0, periodStartUtc, periodStartUtc,
            periodEndUtc, generatedUtc, generatedByUserId, advanceCursor: false, guiBaseUrl, ct);

    private static async Task<CatalogNotificationDigest> GenerateAsync(
        CatalogDbContext catalog, string origin, long afterEventId, DateTime? fromUtc, DateTime periodStartUtc,
        DateTime periodEndUtc, DateTime generatedUtc, Guid? generatedByUserId, bool advanceCursor,
        string? guiBaseUrl, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var events = await NotificationStore.ListEventsForDigestAsync(
            catalog, afterEventId, fromUtc, periodEndUtc, MaxEventsPerDigest + 1, ct).ConfigureAwait(false);
        var truncated = events.Count > MaxEventsPerDigest;
        if (truncated)
        {
            events.RemoveAt(events.Count - 1);
        }

        // The channel is inert here: ComposeReport renders the text, HTML and Block Kit bodies whatever it says,
        // so the one artifact can later be viewed in the GUI or delivered over either channel.
        var message = NotificationComposer.ComposeReport(new NotificationComposition(
            NotificationChannels.Email, NotificationModes.Digest, events, truncated, guiBaseUrl, periodEndUtc,
            new NotificationWindow(periodStartUtc, periodEndUtc)));

        // The same grouping the bodies were rendered from, persisted so the GUI's table and the delivered message
        // can never disagree, and so the digest still reads in full after its events have been pruned.
        var groups = NotificationDigestGroups.Serialize(NotificationComposer.Group(events));

        var digest = new CatalogNotificationDigest
        {
            Id = Guid.CreateVersion7(),
            Origin = origin,
            PeriodStartUtc = periodStartUtc,
            PeriodEndUtc = periodEndUtc,
            GeneratedUtc = generatedUtc,
            GeneratedByUserId = generatedByUserId,
            EventCount = events.Count,
            FlowCount = events.Select(e => e.FlowName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            FailedCount = events.Count(e => e.Kind == NotificationEventKinds.RunFailed),
            CancelledCount = events.Count(e => e.Kind == NotificationEventKinds.RunCancelled),
            SkippedCount = events.Count(e => e.Kind == NotificationEventKinds.RunSkipped),
            AssertionFailedCount = events.Count(e => e.Kind == NotificationEventKinds.AssertionFailed),
            FirstEventId = events.Count == 0 ? 0 : events[0].Id,
            LastEventId = events.Count == 0 ? 0 : events[^1].Id,
            Truncated = truncated,
            Subject = message.Subject,
            TextBody = message.TextBody,
            // ComposeReport always renders both; the entity stores them non-null because a digest must stay
            // deliverable to either channel after the events behind it have aged out.
            HtmlBody = message.HtmlBody ?? message.TextBody,
            SlackBlocksJson = message.SlackBlocksJson ?? "[]",
            GroupsJson = groups,
        };

        // An empty window leaves the cursor where it was: there was nothing to consider, so nothing to skip past.
        var newCursor = events.Count == 0 ? afterEventId : events[^1].Id;
        await NotificationStore.SaveDigestAsync(catalog, digest, advanceCursor ? newCursor : null, ct)
            .ConfigureAwait(false);
        return digest;
    }
}
