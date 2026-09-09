using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace SqlFlow.Catalog;

/// <summary>The outcome of one detection tick: whether a window was actually scanned (false when another replica
/// holds a fresher watermark or the single watermark row was just bootstrapped) and how many new events of each
/// family were recorded.</summary>
public sealed record NotificationDetectionResult(bool Scanned, int RunEvents, int AssertionEvents);

/// <summary>One estate digest as a list reads it: identity, window, headline and counts, without the rendered
/// bodies (kilobytes each) that only a reader opening the digest needs.</summary>
public sealed record NotificationDigestSummary(
    Guid Id, string Origin, DateTime PeriodStartUtc, DateTime PeriodEndUtc, DateTime GeneratedUtc,
    Guid? GeneratedByUserId, string Subject, int EventCount, int FlowCount, int FailedCount, int CancelledCount,
    int SkippedCount, int AssertionFailedCount, bool Truncated);

/// <summary>
/// Data access for the notification pipeline: event detection, subscription dispatch claims, and the delivery
/// outbox. Stateless (pure operations over the supplied context and clock), like the other catalog stores. The
/// multi-node discipline matches the rest of the catalog: detection is serialized by an update lock on the single
/// watermark row, a subscription window is claimed by compare-and-swapping its next-due instant (the schedule
/// pattern), and a delivery send is claimed by compare-and-swapping its status (the work-queue pattern), so any
/// number of control-plane replicas can run the notification service without double-detecting or double-sending.
/// </summary>
public static class NotificationStore
{
    /// <summary>How many rows one retention DELETE takes per statement, keeping the lock footprint and log growth
    /// of a prune bounded no matter how large the backlog is.</summary>
    public const int PurgeBatchSize = 5000;

    /// <summary>How many retention batches one housekeeping pass may run per table, so a huge backlog is worked
    /// off across ticks instead of monopolizing one.</summary>
    private const int MaxPurgeBatchesPerSweep = 10;

    // One atomic batch: read-and-lock the watermark, scan the window, insert the missing events, advance the mark.
    //
    // The UPDLOCK + HOLDLOCK read of the single watermark row is what serializes detection across control-plane
    // replicas: a concurrent tick blocks on the row until this one commits, then sees the advanced mark and scans
    // nothing. XACT_ABORT makes any mid-batch error roll the whole tick back (watermark included), so a failed tick
    // is simply retried by the next poll. The leading SET pins READ COMMITTED because a pooled connection can carry
    // a leftover SERIALIZABLE level from a prior transaction.
    //
    // The window scans Run.WrittenUtc, which every terminal transition stamps with the writer's clock; the lower
    // bound backs off by the overlap so a writer whose clock trails the previous tick's upper bound (or whose
    // detail rows land moments after its run row) is still seen. Re-scanning the overlap is free: the NOT EXISTS
    // guards (backed by the unique (RunId, Kind) index) insert each event at most once.
    //
    // Runs recorded long after the fact (an old artifact synced in by `db sync` carries its original WrittenUtc)
    // fall outside the window by design: notifications are about what just went wrong, not about history arriving.
    //
    // The lifecycle gate: a run whose pipeline declares `lifecycle: development` produces no event at all, so a
    // flow under active development can fail freely without paging anyone. The gate is deliberately a NOT EXISTS
    // on a non-production row: a run whose pipeline row is GONE (the flow left the estate after the run was
    // queued) still alerts, because that failure is real and nobody declared it development.
    private const string DetectSql = """
        SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
        SET XACT_ABORT ON;
        DECLARE @runEvents int = 0, @assertionEvents int = 0, @scanned bit = 0;
        BEGIN TRANSACTION;
        DECLARE @since datetime2(7);
        SELECT @since = [RunsWatermarkUtc] FROM [catalog].[NotificationWatermark] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = @watermarkId;
        IF @since IS NULL
        BEGIN
            -- Bootstrap: the mark starts at now, so nothing that predates enabling notifications is evented.
            INSERT INTO [catalog].[NotificationWatermark] ([Id], [RunsWatermarkUtc], [UpdatedUtc])
            VALUES (@watermarkId, @now, @now);
        END
        ELSE IF @since < @now
        BEGIN
            SET @scanned = 1;
            DECLARE @low datetime2(7) = DATEADD(second, -@overlapSeconds, @since);

            INSERT INTO [catalog].[NotificationEvent]
                ([Kind], [RunId], [RepoId], [PipelineId], [GroupId], [FlowName], [FlowKind], [OccurredUtc], [DetectedUtc], [Error])
            SELECT
                CASE r.[Status] WHEN @statusFailed THEN @kindRunFailed WHEN @statusCancelled THEN @kindRunCancelled ELSE @kindRunSkipped END,
                r.[RunId], r.[RepoId], r.[PipelineId], r.[GroupId], r.[FlowName], r.[FlowKind],
                COALESCE(r.[EndUtc], r.[WrittenUtc]), @now, r.[Error]
            FROM [catalog].[Run] AS r
            WHERE r.[Status] IN (@statusFailed, @statusCancelled, @statusSkipped)
              AND r.[WrittenUtc] > @low AND r.[WrittenUtc] <= @now
              AND NOT EXISTS (
                  SELECT 1 FROM [catalog].[Pipeline] AS p
                  WHERE p.[Id] = r.[PipelineId] AND p.[Lifecycle] <> @lifecycleProduction)
              AND NOT EXISTS (
                  SELECT 1 FROM [catalog].[NotificationEvent] AS e
                  WHERE e.[RunId] = r.[RunId] AND e.[Kind] <> @kindAssertionFailed);
            SET @runEvents = @@ROWCOUNT;

            -- Assertions are log-only: a run whose assertions failed still reads succeeded, so it needs its own
            -- event. Failed runs are excluded here (their run_failed event already carries the alert).
            INSERT INTO [catalog].[NotificationEvent]
                ([Kind], [RunId], [RepoId], [PipelineId], [GroupId], [FlowName], [FlowKind], [OccurredUtc], [DetectedUtc], [Error])
            SELECT
                @kindAssertionFailed, r.[RunId], r.[RepoId], r.[PipelineId], r.[GroupId], r.[FlowName], r.[FlowKind],
                COALESCE(r.[EndUtc], r.[WrittenUtc]), @now,
                LEFT((SELECT STRING_AGG(CAST(a.[Name] + N': ' + COALESCE(a.[Error], N'the assertion could not be evaluated') AS nvarchar(max)), N'; ')
                      FROM [catalog].[RunAssertion] AS a
                      WHERE a.[RunId] = r.[RunId] AND a.[Evaluated] = 0), 4000)
            FROM [catalog].[Run] AS r
            WHERE r.[Status] = @statusSucceeded
              AND r.[WrittenUtc] > @low AND r.[WrittenUtc] <= @now
              AND EXISTS (SELECT 1 FROM [catalog].[RunAssertion] AS a WHERE a.[RunId] = r.[RunId] AND a.[Evaluated] = 0)
              AND NOT EXISTS (
                  SELECT 1 FROM [catalog].[Pipeline] AS p
                  WHERE p.[Id] = r.[PipelineId] AND p.[Lifecycle] <> @lifecycleProduction)
              AND NOT EXISTS (
                  SELECT 1 FROM [catalog].[NotificationEvent] AS e
                  WHERE e.[RunId] = r.[RunId] AND e.[Kind] = @kindAssertionFailed);
            SET @assertionEvents = @@ROWCOUNT;

            UPDATE [catalog].[NotificationWatermark] SET [RunsWatermarkUtc] = @now, [UpdatedUtc] = @now WHERE [Id] = @watermarkId;
        END
        COMMIT TRANSACTION;
        SELECT @scanned AS Scanned, @runEvents AS RunEvents, @assertionEvents AS AssertionEvents;
        """;

    /// <summary>
    /// One detection tick: scans the run history from a little before the watermark up to <paramref name="nowUtc"/>
    /// and records a <see cref="CatalogNotificationEvent"/> for every non-success terminal run and every succeeded
    /// run with failed assertions that is not already evented, then advances the mark. Atomic and serialized across
    /// replicas (see <see cref="DetectSql"/>); safe to call every tick regardless of subscriber count, so the event
    /// stream is always current when someone opts in.
    /// </summary>
    public static async Task<NotificationDetectionResult> DetectAsync(
        CatalogDbContext catalog, DateTime nowUtc, TimeSpan overlap, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (overlap <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(overlap), overlap, "The detection overlap must be positive.");
        }

        var strategy = catalog.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            var connection = catalog.Database.GetDbConnection();
            await catalog.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = DetectSql;
                if (catalog.Database.CurrentTransaction is { } tx)
                {
                    command.Transaction = tx.GetDbTransaction();
                }

                AddParameter(command, "@watermarkId", CatalogNotificationWatermark.WellKnownId);
                AddParameter(command, "@now", nowUtc);
                AddParameter(command, "@overlapSeconds", (int)Math.Ceiling(overlap.TotalSeconds));
                AddParameter(command, "@statusFailed", RunStatuses.Failed);
                AddParameter(command, "@statusCancelled", RunStatuses.Cancelled);
                AddParameter(command, "@statusSkipped", RunStatuses.Skipped);
                AddParameter(command, "@statusSucceeded", RunStatuses.Succeeded);
                AddParameter(command, "@kindRunFailed", NotificationEventKinds.RunFailed);
                AddParameter(command, "@kindRunCancelled", NotificationEventKinds.RunCancelled);
                AddParameter(command, "@kindRunSkipped", NotificationEventKinds.RunSkipped);
                AddParameter(command, "@kindAssertionFailed", NotificationEventKinds.AssertionFailed);
                AddParameter(command, "@lifecycleProduction", PipelineLifecycles.Production);

                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("The notification detection batch returned no result row.");
                }

                return new NotificationDetectionResult(
                    reader.GetBoolean(0), reader.GetInt32(1), reader.GetInt32(2));
            }
            finally
            {
                await catalog.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    /// <summary>The newest event id, or 0 when no event has ever been recorded: the snapshot a dispatch tick works
    /// against and the starting cursor for a new subscription (so opting in never replays history).</summary>
    public static async Task<long> MaxEventIdAsync(CatalogDbContext catalog, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return await catalog.NotificationEvents.AsNoTracking()
            .MaxAsync(e => (long?)e.Id, ct).ConfigureAwait(false) ?? 0;
    }

    /// <summary>A subscription's pending slice of the event stream: events after its cursor, up to the tick's
    /// snapshot, oldest first, capped at <paramref name="take"/>. Callers pass the cap + 1 to learn whether the
    /// slice was truncated (and advance the cursor only over what they actually took).</summary>
    public static async Task<List<CatalogNotificationEvent>> ListEventsAfterAsync(
        CatalogDbContext catalog, long afterId, long maxId, int take, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return await catalog.NotificationEvents.AsNoTracking()
            .Where(e => e.Id > afterId && e.Id <= maxId)
            .OrderBy(e => e.Id)
            .Take(Math.Clamp(take, 1, 5000))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The subscriptions a dispatch tick should consider: enabled, past (or without) their next-due instant, and,
    /// for immediate mode, holding pending events (a digest is claimed even when idle, so its window keeps
    /// advancing and an empty window sends nothing). Each result is still only acted on after
    /// <see cref="TryClaimSubscriptionAsync"/> wins the compare-and-swap.
    /// </summary>
    public static async Task<IReadOnlyList<CatalogNotificationSubscription>> ListDueSubscriptionsAsync(
        CatalogDbContext catalog, DateTime nowUtc, long maxEventId, int max, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return await catalog.NotificationSubscriptions.AsNoTracking()
            .Where(s => s.Enabled
                && (s.NextDueUtc == null || s.NextDueUtc <= nowUtc)
                && (s.Mode == NotificationModes.Digest || s.LastEventId < maxEventId))
            .OrderBy(s => s.NextDueUtc)
            .Take(Math.Clamp(max, 1, 1000))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Atomically claims a subscription's send window by advancing <see cref="CatalogNotificationSubscription.NextDueUtc"/>
    /// from the value the caller observed. Returns true only for the winner: when several control-plane nodes scan
    /// the same due subscription, exactly one composes its message (the schedule-fire pattern). The claim re-checks
    /// <c>Enabled</c> so a subscription disabled between scan and claim is never sent.
    /// </summary>
    public static async Task<bool> TryClaimSubscriptionAsync(
        CatalogDbContext catalog, Guid id, DateTime? observedNextDueUtc, DateTime? newNextDueUtc, DateTime nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var affected = await catalog.NotificationSubscriptions
            .Where(s => s.Id == id && s.Enabled && s.NextDueUtc == observedNextDueUtc)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.NextDueUtc, newNextDueUtc)
                .SetProperty(x => x.UpdatedUtc, nowUtc), ct)
            .ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>
    /// Finishes a claimed dispatch atomically: advances the subscription's cursor (never backwards) and, when a
    /// message was composed, stamps the send time and enqueues the delivery, all in one transaction, so a crash can
    /// only ever repeat a window (at-least-once), never lose the record of what a sent message covered. A
    /// subscription deleted mid-dispatch drops the message: nothing should go out for an opt-in that no longer exists.
    /// </summary>
    public static async Task CompleteDispatchAsync(
        CatalogDbContext catalog, Guid subscriptionId, long newLastEventId, DateTime? sentUtc,
        CatalogNotificationDelivery? delivery, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var subscription = await catalog.NotificationSubscriptions.AsTracking()
            .FirstOrDefaultAsync(s => s.Id == subscriptionId, ct).ConfigureAwait(false);
        if (subscription is null)
        {
            return;
        }

        if (subscription.LastEventId < newLastEventId)
        {
            subscription.LastEventId = newLastEventId;
        }

        if (sentUtc is not null)
        {
            subscription.LastSentUtc = sentUtc;
        }

        subscription.UpdatedUtc = nowUtc;
        if (delivery is not null)
        {
            catalog.NotificationDeliveries.Add(delivery);
        }

        await catalog.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The owner's subscriptions, oldest first (creation order reads naturally in a settings list).</summary>
    public static async Task<IReadOnlyList<CatalogNotificationSubscription>> ListSubscriptionsForUserAsync(
        CatalogDbContext catalog, Guid userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return await catalog.NotificationSubscriptions.AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderBy(s => s.CreatedUtc).ThenBy(s => s.Id)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>One subscription, scoped to its owner (a caller can never read another user's opt-in).</summary>
    public static async Task<CatalogNotificationSubscription?> GetSubscriptionForUserAsync(
        CatalogDbContext catalog, Guid userId, Guid id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return await catalog.NotificationSubscriptions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct).ConfigureAwait(false);
    }

    /// <summary>Inserts a new subscription with its cursor fast-forwarded to the newest existing event, so the
    /// first message covers only what happens after the opt-in.</summary>
    public static async Task CreateSubscriptionAsync(
        CatalogDbContext catalog, CatalogNotificationSubscription subscription, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(subscription);
        subscription.LastEventId = await MaxEventIdAsync(catalog, ct).ConfigureAwait(false);
        catalog.NotificationSubscriptions.Add(subscription);
        await catalog.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Persists an edited subscription (the caller loaded it, applied the changes, and recomputed the
    /// pacing state). <paramref name="fastForwardCursor"/> is set when the subscription was just re-enabled: the
    /// cursor jumps past everything that happened while it was off, so resuming never floods.</summary>
    public static async Task SaveSubscriptionAsync(
        CatalogDbContext catalog, CatalogNotificationSubscription subscription, bool fastForwardCursor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(subscription);
        if (fastForwardCursor)
        {
            subscription.LastEventId = await MaxEventIdAsync(catalog, ct).ConfigureAwait(false);
        }

        catalog.NotificationSubscriptions.Update(subscription);
        await catalog.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Deletes a subscription, scoped to its owner. Its delivery history stays (soft link), so "what was
    /// sent while it existed" remains auditable. Returns false when no such subscription belongs to the caller.</summary>
    public static async Task<bool> DeleteSubscriptionForUserAsync(
        CatalogDbContext catalog, Guid userId, Guid id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var affected = await catalog.NotificationSubscriptions
            .Where(s => s.Id == id && s.UserId == userId)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>Inserts an already-composed delivery onto the outbox (the dispatcher's normal path goes through
    /// <see cref="CompleteDispatchAsync"/>; this is for messages with no event window, like a test send).</summary>
    public static async Task EnqueueDeliveryAsync(
        CatalogDbContext catalog, CatalogNotificationDelivery delivery, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(delivery);
        catalog.NotificationDeliveries.Add(delivery);
        await catalog.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Queued deliveries whose next attempt is due, oldest first. Each is only sent after
    /// <see cref="TryClaimDeliverySendAsync"/> wins its status compare-and-swap.</summary>
    public static async Task<IReadOnlyList<CatalogNotificationDelivery>> ListSendableDeliveriesAsync(
        CatalogDbContext catalog, DateTime nowUtc, int max, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return await catalog.NotificationDeliveries.AsNoTracking()
            .Where(d => d.Status == NotificationDeliveryStatuses.Queued
                && (d.NextAttemptUtc == null || d.NextAttemptUtc <= nowUtc))
            .OrderBy(d => d.CreatedUtc).ThenBy(d => d.Id)
            .Take(Math.Clamp(max, 1, 500))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Atomically claims a delivery for sending (queued to sending, attempt counted, claim stamped).
    /// Returns true only for the winner, so concurrent replicas never double-send one message.</summary>
    public static async Task<bool> TryClaimDeliverySendAsync(
        CatalogDbContext catalog, Guid id, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var affected = await catalog.NotificationDeliveries
            .Where(d => d.Id == id && d.Status == NotificationDeliveryStatuses.Queued)
            .ExecuteUpdateAsync(d => d
                .SetProperty(x => x.Status, NotificationDeliveryStatuses.Sending)
                .SetProperty(x => x.Attempts, x => x.Attempts + 1)
                .SetProperty(x => x.ClaimedUtc, nowUtc), ct)
            .ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>Records a claimed delivery as accepted by its channel.</summary>
    public static Task MarkDeliverySentAsync(
        CatalogDbContext catalog, Guid id, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.NotificationDeliveries
            .Where(d => d.Id == id && d.Status == NotificationDeliveryStatuses.Sending)
            .ExecuteUpdateAsync(d => d
                .SetProperty(x => x.Status, NotificationDeliveryStatuses.Sent)
                .SetProperty(x => x.SentUtc, nowUtc)
                .SetProperty(x => x.ClaimedUtc, (DateTime?)null)
                .SetProperty(x => x.LastError, (string?)null), ct);
    }

    /// <summary>Returns a claimed delivery to the queue for a later attempt, recording why this one failed.</summary>
    public static Task RequeueDeliveryAsync(
        CatalogDbContext catalog, Guid id, string error, DateTime nextAttemptUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return catalog.NotificationDeliveries
            .Where(d => d.Id == id && d.Status == NotificationDeliveryStatuses.Sending)
            .ExecuteUpdateAsync(d => d
                .SetProperty(x => x.Status, NotificationDeliveryStatuses.Queued)
                .SetProperty(x => x.NextAttemptUtc, nextAttemptUtc)
                .SetProperty(x => x.ClaimedUtc, (DateTime?)null)
                .SetProperty(x => x.LastError, error), ct);
    }

    /// <summary>Records a claimed delivery as permanently failed (attempts exhausted or the failure is not
    /// retryable). The row stays for the owner's history; nothing is silently dropped.</summary>
    public static Task MarkDeliveryFailedAsync(
        CatalogDbContext catalog, Guid id, string error, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return catalog.NotificationDeliveries
            .Where(d => d.Id == id && d.Status == NotificationDeliveryStatuses.Sending)
            .ExecuteUpdateAsync(d => d
                .SetProperty(x => x.Status, NotificationDeliveryStatuses.Failed)
                .SetProperty(x => x.ClaimedUtc, (DateTime?)null)
                .SetProperty(x => x.LastError, error), ct);
    }

    /// <summary>Requeues deliveries stuck in <c>sending</c> since before <paramref name="claimedBeforeUtc"/>: the
    /// claiming node died mid-send. The attempt was already counted at claim time, so a repeatedly-dying send still
    /// converges to <c>failed</c> instead of looping forever. Returns how many were recovered.</summary>
    public static Task<int> RecoverStuckDeliveriesAsync(
        CatalogDbContext catalog, DateTime claimedBeforeUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.NotificationDeliveries
            .Where(d => d.Status == NotificationDeliveryStatuses.Sending
                && d.ClaimedUtc != null && d.ClaimedUtc < claimedBeforeUtc)
            .ExecuteUpdateAsync(d => d
                .SetProperty(x => x.Status, NotificationDeliveryStatuses.Queued)
                .SetProperty(x => x.ClaimedUtc, (DateTime?)null)
                .SetProperty(x => x.LastError, "the send was interrupted (the sending node stopped mid-attempt) and was requeued"), ct);
    }

    /// <summary>The owner's recent deliveries, newest first: the self-service answer to "did my notification
    /// actually go out, and if not, why".</summary>
    public static async Task<IReadOnlyList<CatalogNotificationDelivery>> ListDeliveriesForUserAsync(
        CatalogDbContext catalog, Guid userId, int take, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return await catalog.NotificationDeliveries.AsNoTracking()
            .Where(d => d.UserId == userId)
            .OrderByDescending(d => d.CreatedUtc).ThenByDescending(d => d.Id)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Prunes aged rows: events detected before <paramref name="eventsBeforeUtc"/>, terminal (sent / failed)
    /// deliveries created before <paramref name="deliveriesBeforeUtc"/>, and digests generated before
    /// <paramref name="digestsBeforeUtc"/>. Queued and sending deliveries are never pruned (undelivered work is
    /// not garbage). Deletes run in bounded batches so a large backlog never takes a long lock; a backlog bigger
    /// than one sweep's budget is finished by later sweeps. Returns the rows removed.
    /// </summary>
    public static async Task<(int Events, int Deliveries, int Digests)> PurgeExpiredAsync(
        CatalogDbContext catalog, DateTime eventsBeforeUtc, DateTime deliveriesBeforeUtc, DateTime digestsBeforeUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var events = 0;
        for (var batch = 0; batch < MaxPurgeBatchesPerSweep; batch++)
        {
            var deleted = await catalog.Database.ExecuteSqlAsync(
                $"DELETE TOP ({PurgeBatchSize}) FROM [catalog].[NotificationEvent] WHERE [DetectedUtc] < {eventsBeforeUtc}",
                ct).ConfigureAwait(false);
            events += deleted;
            if (deleted < PurgeBatchSize)
            {
                break;
            }
        }

        var deliveries = 0;
        for (var batch = 0; batch < MaxPurgeBatchesPerSweep; batch++)
        {
            var deleted = await catalog.Database.ExecuteSqlAsync(
                $"""
                 DELETE TOP ({PurgeBatchSize}) FROM [catalog].[NotificationDelivery]
                 WHERE [CreatedUtc] < {deliveriesBeforeUtc}
                   AND [Status] IN ({NotificationDeliveryStatuses.Sent}, {NotificationDeliveryStatuses.Failed})
                 """,
                ct).ConfigureAwait(false);
            deliveries += deleted;
            if (deleted < PurgeBatchSize)
            {
                break;
            }
        }

        var digests = 0;
        for (var batch = 0; batch < MaxPurgeBatchesPerSweep; batch++)
        {
            var deleted = await catalog.Database.ExecuteSqlAsync(
                $"DELETE TOP ({PurgeBatchSize}) FROM [catalog].[NotificationDigest] WHERE [GeneratedUtc] < {digestsBeforeUtc}",
                ct).ConfigureAwait(false);
            digests += deleted;
            if (deleted < PurgeBatchSize)
            {
                break;
            }
        }

        return (events, deliveries, digests);
    }

    /// <summary>The notification pipeline's single state row, or null before the first detection tick creates it.
    /// The digest generator reads it to learn whether its window is due and where the previous one ended.</summary>
    public static Task<CatalogNotificationWatermark?> GetWatermarkAsync(
        CatalogDbContext catalog, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.NotificationWatermarks.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == CatalogNotificationWatermark.WellKnownId, ct);
    }

    /// <summary>
    /// Atomically claims the scheduled digest window by advancing <see cref="CatalogNotificationWatermark.DigestDueUtc"/>
    /// from the value the caller observed, and stamps the next window's start. Returns true only for the winner, so
    /// when several control-plane replicas see the same window come due, exactly one generates its digest (the same
    /// compare-and-swap the subscription dispatcher uses).
    /// </summary>
    public static async Task<bool> TryClaimDigestWindowAsync(
        CatalogDbContext catalog, DateTime? observedDueUtc, DateTime newDueUtc, DateTime nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var affected = await catalog.NotificationWatermarks
            .Where(w => w.Id == CatalogNotificationWatermark.WellKnownId && w.DigestDueUtc == observedDueUtc)
            .ExecuteUpdateAsync(w => w
                .SetProperty(x => x.DigestDueUtc, newDueUtc)
                .SetProperty(x => x.DigestPeriodStartUtc, nowUtc)
                .SetProperty(x => x.UpdatedUtc, nowUtc), ct)
            .ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>
    /// The events one digest covers, oldest first: everything after <paramref name="afterEventId"/> that was
    /// detected no later than <paramref name="toUtc"/>, optionally floored at <paramref name="fromUtc"/>. The
    /// scheduled generator passes its cursor and no floor (so nothing detected late is ever skipped); a manual
    /// generation passes a cursor of 0 and the window the caller asked for. Callers pass their cap + 1 to learn
    /// whether the slice was truncated.
    /// </summary>
    public static async Task<List<CatalogNotificationEvent>> ListEventsForDigestAsync(
        CatalogDbContext catalog, long afterEventId, DateTime? fromUtc, DateTime toUtc, int take,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var query = catalog.NotificationEvents.AsNoTracking()
            .Where(e => e.Id > afterEventId && e.DetectedUtc <= toUtc);
        if (fromUtc is { } floor)
        {
            query = query.Where(e => e.DetectedUtc >= floor);
        }

        return await query
            .OrderBy(e => e.Id)
            .Take(Math.Clamp(take, 1, 5000))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Persists a generated digest and, for a scheduled one, advances the generator's cursor to what the digest
    /// actually covered, in a single transaction: a crash can only ever repeat a window, never leave a covered
    /// window unrecorded. <paramref name="newCursorEventId"/> is null for a manual digest, which is a report over
    /// a window the caller chose and must never rob the next scheduled one. The cursor never moves backwards.
    /// </summary>
    public static async Task SaveDigestAsync(
        CatalogDbContext catalog, CatalogNotificationDigest digest, long? newCursorEventId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(digest);
        catalog.NotificationDigests.Add(digest);
        if (newCursorEventId is { } cursor)
        {
            var watermark = await catalog.NotificationWatermarks.AsTracking()
                .FirstOrDefaultAsync(w => w.Id == CatalogNotificationWatermark.WellKnownId, ct).ConfigureAwait(false);
            if (watermark is not null && watermark.DigestCursorEventId < cursor)
            {
                watermark.DigestCursorEventId = cursor;
                watermark.UpdatedUtc = digest.GeneratedUtc;
            }
        }

        await catalog.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The digest list as the GUI shows it, newest first: headline and counts without the rendered
    /// bodies, which are several kilobytes each and are only read when one digest is opened.</summary>
    public static async Task<IReadOnlyList<NotificationDigestSummary>> ListDigestsAsync(
        CatalogDbContext catalog, int take, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return await catalog.NotificationDigests.AsNoTracking()
            .OrderByDescending(d => d.GeneratedUtc).ThenByDescending(d => d.Id)
            .Take(Math.Clamp(take, 1, 200))
            .Select(d => new NotificationDigestSummary(
                d.Id, d.Origin, d.PeriodStartUtc, d.PeriodEndUtc, d.GeneratedUtc, d.GeneratedByUserId, d.Subject,
                d.EventCount, d.FlowCount, d.FailedCount, d.CancelledCount, d.SkippedCount, d.AssertionFailedCount,
                d.Truncated))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>One digest with its rendered bodies, or null when it has been pruned or never existed.</summary>
    public static Task<CatalogNotificationDigest?> GetDigestAsync(
        CatalogDbContext catalog, Guid id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.NotificationDigests.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
