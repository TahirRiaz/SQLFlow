using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The notification pipeline's data layer (<see cref="NotificationStore"/>) against the real catalog database:
/// detection turns terminal runs and failed assertions into deduplicated events (honoring the pipeline lifecycle
/// gate), subscription windows and outbox sends are claimed exactly once, cursors only move forward, stuck sends
/// recover, and retention prunes the aged rows while never touching queued work. The assembly runs serially, so
/// detection windows only ever contain this test's rows plus other tests' leftovers, which every assertion here
/// is scoped away from (asserting on this test's run ids, never on global counts). Each test removes its rows.
/// </summary>
[Trait("Category", "Integration")]
public sealed class NotificationStoreTests
{
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(30);

    [SkippableFact]
    public async Task Detect_RecordsAFailedRun_ExactlyOnce()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var db = CatalogDatabase.Create(cs);
        var run = NewRun(RunStatuses.Failed, error: "the target table vanished");

        try
        {
            // First call either bootstraps the watermark or scans; either way the mark is current afterwards.
            await NotificationStore.DetectAsync(db, DateTime.UtcNow, Overlap);

            db.Runs.Add(run);
            await db.SaveChangesAsync();

            await NotificationStore.DetectAsync(db, DateTime.UtcNow, Overlap);
            var events = await EventsFor(db, run.RunId);
            var single = Assert.Single(events);
            Assert.Equal(NotificationEventKinds.RunFailed, single.Kind);
            Assert.Equal(run.FlowName, single.FlowName);
            Assert.Equal("the target table vanished", single.Error);

            // Re-detection re-scans the overlap window; the unique (run, kind) guard keeps it at one event.
            await NotificationStore.DetectAsync(db, DateTime.UtcNow, Overlap);
            Assert.Single(await EventsFor(db, run.RunId));
        }
        finally
        {
            await CleanupAsync(db, run.RunId);
        }
    }

    [SkippableFact]
    public async Task Detect_MapsCancelledAndSkipped_ToTheirKinds()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var db = CatalogDatabase.Create(cs);
        var cancelled = NewRun(RunStatuses.Cancelled, error: "cancelled by an operator");
        var skipped = NewRun(RunStatuses.Skipped, error: "skipped: an upstream dependency did not succeed.");

        try
        {
            await NotificationStore.DetectAsync(db, DateTime.UtcNow, Overlap);
            db.Runs.AddRange(cancelled, skipped);
            await db.SaveChangesAsync();

            await NotificationStore.DetectAsync(db, DateTime.UtcNow, Overlap);
            Assert.Equal(NotificationEventKinds.RunCancelled, Assert.Single(await EventsFor(db, cancelled.RunId)).Kind);
            Assert.Equal(NotificationEventKinds.RunSkipped, Assert.Single(await EventsFor(db, skipped.RunId)).Kind);
        }
        finally
        {
            await CleanupAsync(db, cancelled.RunId);
            await CleanupAsync(db, skipped.RunId);
        }
    }

    [SkippableFact]
    public async Task Detect_RecordsFailedAssertions_OnAGreenRun()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var db = CatalogDatabase.Create(cs);
        var green = NewRun(RunStatuses.Succeeded, error: null, success: true);
        var fullyGreen = NewRun(RunStatuses.Succeeded, error: null, success: true);

        try
        {
            await NotificationStore.DetectAsync(db, DateTime.UtcNow, Overlap);
            db.Runs.AddRange(green, fullyGreen);
            db.RunAssertions.Add(new CatalogRunAssertion
            {
                RunId = green.RunId,
                Name = "row-count-positive",
                Result = "0",
                AssertedValue = string.Empty,
                Evaluated = false,
                Error = "Invalid object name 'dbo.Missing'.",
            });
            db.RunAssertions.Add(new CatalogRunAssertion
            {
                RunId = fullyGreen.RunId,
                Name = "row-count-positive",
                Result = "42",
                AssertedValue = "42",
                Evaluated = true,
            });
            await db.SaveChangesAsync();

            await NotificationStore.DetectAsync(db, DateTime.UtcNow, Overlap);

            var evt = Assert.Single(await EventsFor(db, green.RunId));
            Assert.Equal(NotificationEventKinds.AssertionFailed, evt.Kind);
            Assert.Contains("row-count-positive", evt.Error, StringComparison.Ordinal);
            Assert.Contains("Invalid object name", evt.Error, StringComparison.Ordinal);

            // A run whose assertions all evaluated stays silent.
            Assert.Empty(await EventsFor(db, fullyGreen.RunId));
        }
        finally
        {
            await CleanupAsync(db, green.RunId);
            await CleanupAsync(db, fullyGreen.RunId);
        }
    }

    [SkippableFact]
    public async Task Detect_HonorsThePipelineLifecycleGate()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var db = CatalogDatabase.Create(cs);

        var repoId = Guid.NewGuid();
        var development = NewPipeline(repoId, PipelineLifecycles.Development);
        var production = NewPipeline(repoId, PipelineLifecycles.Production);
        var devRun = NewRun(RunStatuses.Failed, error: "still under construction", pipelineId: development.Id);
        var prodRun = NewRun(RunStatuses.Failed, error: "a real failure", pipelineId: production.Id);
        var orphanRun = NewRun(RunStatuses.Failed, error: "its pipeline left the estate");

        try
        {
            await NotificationStore.DetectAsync(db, DateTime.UtcNow, Overlap);
            db.Pipelines.AddRange(development, production);
            db.Runs.AddRange(devRun, prodRun, orphanRun);
            await db.SaveChangesAsync();

            await NotificationStore.DetectAsync(db, DateTime.UtcNow, Overlap);

            // The development pipeline's failure produces nothing; the production one and the orphan (no
            // pipeline row at all: nobody declared it development) both alert.
            Assert.Empty(await EventsFor(db, devRun.RunId));
            Assert.Single(await EventsFor(db, prodRun.RunId));
            Assert.Single(await EventsFor(db, orphanRun.RunId));
        }
        finally
        {
            await CleanupAsync(db, devRun.RunId);
            await CleanupAsync(db, prodRun.RunId);
            await CleanupAsync(db, orphanRun.RunId);
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task SubscriptionClaim_IsWonExactlyOnce_AndTheCursorNeverRegresses()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var db = CatalogDatabase.Create(cs);
        var userId = Guid.NewGuid();
        var subscription = NewSubscription(userId);

        try
        {
            await NotificationStore.CreateSubscriptionAsync(db, subscription);
            var now = DateTime.UtcNow;
            var cooldownEnd = now.AddMinutes(5);

            Assert.True(await NotificationStore.TryClaimSubscriptionAsync(db, subscription.Id, null, cooldownEnd, now));
            // The losing replica observed the same pre-claim value and must lose.
            Assert.False(await NotificationStore.TryClaimSubscriptionAsync(db, subscription.Id, null, cooldownEnd.AddMinutes(1), now));

            // A new subscription starts at the estate's current event id, so the cursor this test moves is
            // relative to that seed: hardcoding an absolute id fails the day the catalog passes it.
            var seeded = (await Reload(db, subscription.Id)).LastEventId;
            var advanced = seeded + 100;

            var delivery = NewDelivery(subscription, userId);
            await NotificationStore.CompleteDispatchAsync(db, subscription.Id, advanced, sentUtc: now, delivery, now);
            var afterSend = await Reload(db, subscription.Id);
            Assert.Equal(advanced, afterSend.LastEventId);
            Assert.Equal(cooldownEnd, afterSend.NextDueUtc);
            Assert.NotNull(afterSend.LastSentUtc);
            Assert.NotNull(await db.NotificationDeliveries.AsNoTracking().FirstOrDefaultAsync(d => d.Id == delivery.Id));

            // A stale completion (a crashed replica's leftover) can never pull the cursor backwards.
            await NotificationStore.CompleteDispatchAsync(db, subscription.Id, advanced - 100, sentUtc: null, null, now);
            Assert.Equal(advanced, (await Reload(db, subscription.Id)).LastEventId);

            // A disabled subscription is never claimable, even when due.
            var disabled = await db.NotificationSubscriptions.AsTracking().FirstAsync(s => s.Id == subscription.Id);
            disabled.Enabled = false;
            await db.SaveChangesAsync();
            Assert.False(await NotificationStore.TryClaimSubscriptionAsync(db, subscription.Id, cooldownEnd, now.AddHours(1), now));
        }
        finally
        {
            await CleanupUserAsync(db, userId);
        }
    }

    [SkippableFact]
    public async Task DueScan_FindsImmediateOnlyWithPendingEvents_AndDigestByScheduleAlone()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var db = CatalogDatabase.Create(cs);
        var userId = Guid.NewGuid();
        var immediate = NewSubscription(userId);
        var digest = NewSubscription(userId);
        digest.Mode = NotificationModes.Digest;
        digest.NextDueUtc = DateTime.UtcNow.AddMinutes(-1);

        try
        {
            await NotificationStore.CreateSubscriptionAsync(db, immediate);
            await NotificationStore.CreateSubscriptionAsync(db, digest);
            var maxEventId = await NotificationStore.MaxEventIdAsync(db);
            var due = await NotificationStore.ListDueSubscriptionsAsync(db, DateTime.UtcNow, maxEventId, 1000);

            // The immediate subscription's cursor was fast-forwarded at creation: nothing pending, not due.
            Assert.DoesNotContain(due, s => s.Id == immediate.Id);
            // The digest is due purely by its clock, pending events or not (an empty window sends nothing).
            Assert.Contains(due, s => s.Id == digest.Id);

            // Rewind the immediate cursor to simulate pending events (when any events exist at all).
            if (maxEventId > 0)
            {
                await db.NotificationSubscriptions.Where(s => s.Id == immediate.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastEventId, maxEventId - 1));
                due = await NotificationStore.ListDueSubscriptionsAsync(db, DateTime.UtcNow, maxEventId, 1000);
                Assert.Contains(due, s => s.Id == immediate.Id);
            }
        }
        finally
        {
            await CleanupUserAsync(db, userId);
        }
    }

    [SkippableFact]
    public async Task DeliveryOutbox_ClaimsSendsRetriesAndRecovers()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var db = CatalogDatabase.Create(cs);
        var userId = Guid.NewGuid();
        var subscription = NewSubscription(userId);

        try
        {
            await NotificationStore.CreateSubscriptionAsync(db, subscription);
            var delivery = NewDelivery(subscription, userId);
            await NotificationStore.EnqueueDeliveryAsync(db, delivery);

            var now = DateTime.UtcNow;
            var sendable = await NotificationStore.ListSendableDeliveriesAsync(db, now, 500);
            Assert.Contains(sendable, d => d.Id == delivery.Id);

            // Exactly one sender wins the claim; the attempt is counted at claim time.
            Assert.True(await NotificationStore.TryClaimDeliverySendAsync(db, delivery.Id, now));
            Assert.False(await NotificationStore.TryClaimDeliverySendAsync(db, delivery.Id, now));
            var claimed = await ReloadDelivery(db, delivery.Id);
            Assert.Equal(NotificationDeliveryStatuses.Sending, claimed.Status);
            Assert.Equal(1, claimed.Attempts);
            Assert.NotNull(claimed.ClaimedUtc);

            // A transient failure goes back to the queue with the reason and a future attempt time.
            var retryAt = now.AddMinutes(1);
            await NotificationStore.RequeueDeliveryAsync(db, delivery.Id, "SMTP connect timed out", retryAt);
            var requeued = await ReloadDelivery(db, delivery.Id);
            Assert.Equal(NotificationDeliveryStatuses.Queued, requeued.Status);
            Assert.Equal("SMTP connect timed out", requeued.LastError);
            Assert.Null(requeued.ClaimedUtc);
            // Not yet due: the backoff keeps it out of the sendable list.
            Assert.DoesNotContain(await NotificationStore.ListSendableDeliveriesAsync(db, now, 500), d => d.Id == delivery.Id);

            // A claim orphaned by a dead node is recovered back to queued.
            Assert.True(await NotificationStore.TryClaimDeliverySendAsync(db, delivery.Id, retryAt));
            var recovered = await NotificationStore.RecoverStuckDeliveriesAsync(db, retryAt.AddMinutes(1));
            Assert.True(recovered >= 1);
            Assert.Equal(NotificationDeliveryStatuses.Queued, (await ReloadDelivery(db, delivery.Id)).Status);

            // A successful send clears the error and stamps the instant.
            Assert.True(await NotificationStore.TryClaimDeliverySendAsync(db, delivery.Id, retryAt));
            await NotificationStore.MarkDeliverySentAsync(db, delivery.Id, retryAt);
            var sent = await ReloadDelivery(db, delivery.Id);
            Assert.Equal(NotificationDeliveryStatuses.Sent, sent.Status);
            Assert.NotNull(sent.SentUtc);
            Assert.Null(sent.LastError);

            // A permanent failure is terminal.
            var doomed = NewDelivery(subscription, userId);
            await NotificationStore.EnqueueDeliveryAsync(db, doomed);
            Assert.True(await NotificationStore.TryClaimDeliverySendAsync(db, doomed.Id, retryAt));
            await NotificationStore.MarkDeliveryFailedAsync(db, doomed.Id, "no Slack account matches that address");
            Assert.Equal(NotificationDeliveryStatuses.Failed, (await ReloadDelivery(db, doomed.Id)).Status);
        }
        finally
        {
            await CleanupUserAsync(db, userId);
        }
    }

    [SkippableFact]
    public async Task Purge_PrunesAgedRows_ButNeverQueuedWork()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var db = CatalogDatabase.Create(cs);
        var userId = Guid.NewGuid();
        var subscription = NewSubscription(userId);

        try
        {
            await NotificationStore.CreateSubscriptionAsync(db, subscription);
            var ancient = DateTime.UtcNow.AddDays(-400);

            var oldSent = NewDelivery(subscription, userId);
            oldSent.Status = NotificationDeliveryStatuses.Sent;
            oldSent.CreatedUtc = ancient;
            var oldQueued = NewDelivery(subscription, userId);
            oldQueued.CreatedUtc = ancient;
            await NotificationStore.EnqueueDeliveryAsync(db, oldSent);
            await NotificationStore.EnqueueDeliveryAsync(db, oldQueued);

            await NotificationStore.PurgeExpiredAsync(
                db, eventsBeforeUtc: DateTime.UtcNow.AddDays(-365), deliveriesBeforeUtc: DateTime.UtcNow.AddDays(-365),
                digestsBeforeUtc: DateTime.UtcNow.AddDays(-365));

            Assert.Null(await db.NotificationDeliveries.AsNoTracking().FirstOrDefaultAsync(d => d.Id == oldSent.Id));
            // Undelivered work is not garbage, no matter how old.
            Assert.NotNull(await db.NotificationDeliveries.AsNoTracking().FirstOrDefaultAsync(d => d.Id == oldQueued.Id));
        }
        finally
        {
            await CleanupUserAsync(db, userId);
        }
    }

    private static CatalogRun NewRun(
        string status, string? error, bool success = false, Guid? pipelineId = null) => new()
    {
        RunId = Guid.CreateVersion7(),
        PipelineId = pipelineId ?? Guid.NewGuid(),
        RepoId = Guid.NewGuid(),
        FlowName = $"notif-store-test-{Guid.NewGuid():N}",
        FlowKind = "file",
        Status = status,
        Success = success,
        Error = error,
        WrittenUtc = DateTime.UtcNow,
        EndUtc = DateTime.UtcNow,
    };

    private static CatalogPipeline NewPipeline(Guid repoId, string lifecycle) => new()
    {
        Id = Guid.NewGuid(),
        RepoId = repoId,
        Name = $"notif-lifecycle-test-{Guid.NewGuid():N}",
        Kind = "file",
        RelativePath = "test.flow.yaml",
        Lifecycle = lifecycle,
        ContentHash = "test",
        Yaml = "test",
        DefinitionJson = "{}",
        Active = true,
        FirstSeenUtc = DateTime.UtcNow,
        LastSeenUtc = DateTime.UtcNow,
    };

    private static CatalogNotificationSubscription NewSubscription(Guid userId) => new()
    {
        Id = Guid.CreateVersion7(),
        UserId = userId,
        Channel = NotificationChannels.Email,
        Mode = NotificationModes.Immediate,
        EmailAddress = "subscriber@example.com",
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };

    private static CatalogNotificationDelivery NewDelivery(
        CatalogNotificationSubscription subscription, Guid userId) => new()
    {
        Id = Guid.CreateVersion7(),
        SubscriptionId = subscription.Id,
        UserId = userId,
        Channel = subscription.Channel,
        Target = "subscriber@example.com",
        Subject = "SQLFlow: flow 'x' failed",
        TextBody = "flow 'x' failed",
        HtmlBody = "<p>flow 'x' failed</p>",
        EventCount = 1,
        FirstEventId = 1,
        LastEventId = 1,
        Status = NotificationDeliveryStatuses.Queued,
        CreatedUtc = DateTime.UtcNow,
    };

    private static Task<List<CatalogNotificationEvent>> EventsFor(CatalogDbContext db, Guid runId)
        => db.NotificationEvents.AsNoTracking().Where(e => e.RunId == runId).ToListAsync();

    private static Task<CatalogNotificationSubscription> Reload(CatalogDbContext db, Guid id)
        => db.NotificationSubscriptions.AsNoTracking().FirstAsync(s => s.Id == id);

    private static Task<CatalogNotificationDelivery> ReloadDelivery(CatalogDbContext db, Guid id)
        => db.NotificationDeliveries.AsNoTracking().FirstAsync(d => d.Id == id);

    private static async Task CleanupAsync(CatalogDbContext db, Guid runId)
    {
        await db.NotificationEvents.Where(e => e.RunId == runId).ExecuteDeleteAsync();
        await db.RunAssertions.Where(a => a.RunId == runId).ExecuteDeleteAsync();
        await db.Runs.Where(r => r.RunId == runId).ExecuteDeleteAsync();
    }

    private static async Task CleanupUserAsync(CatalogDbContext db, Guid userId)
    {
        await db.NotificationDeliveries.Where(d => d.UserId == userId).ExecuteDeleteAsync();
        await db.NotificationSubscriptions.Where(s => s.UserId == userId).ExecuteDeleteAsync();
    }
}
