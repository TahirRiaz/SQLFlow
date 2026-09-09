using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.ControlPlane.Notifications;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The estate digest: the standing record of what each window held, produced whether or not anybody subscribes.
/// The pure half covers the boundary clock, the all-clear composition an empty window produces, and the persisted
/// grouping the GUI tabulates; the database-backed half covers the exactly-once window claim, the cursor that
/// makes a scheduled digest lossless, and the on-demand digest that must never disturb it.
/// </summary>
public sealed class NotificationDigestTests
{
    private static readonly DateTime Now = new(2026, 7, 11, 12, 34, 0, DateTimeKind.Utc);

    // ---- the boundary clock --------------------------------------------------------------------------------

    private static CatalogNotificationEvent Event(
        long id, string kind = NotificationEventKinds.RunFailed, string flow = "orders-load",
        string? error = "Timeout expired.") => new()
    {
        Id = id,
        Kind = kind,
        RunId = Guid.NewGuid(),
        PipelineId = Guid.NewGuid(),
        FlowName = flow,
        FlowKind = "ing",
        // Occurrence follows the id, as detection assigns them: the higher id is the more recent event.
        OccurredUtc = Now.AddMinutes(id - 600),
        DetectedUtc = Now,
        Error = error,
    };

    [Fact]
    public void Boundary_IsAlignedToTheClock_NotToProcessStart()
    {
        // Daily, no offset: the next boundary is the coming midnight, whatever time the host happened to start.
        Assert.Equal(
            new DateTime(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc),
            NotificationService.NextDigestBoundary(Now, 1440, 0));

        // The same instant, half an hour later, still lands on the same boundary: the rhythm is the clock's.
        Assert.Equal(
            new DateTime(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc),
            NotificationService.NextDigestBoundary(Now.AddMinutes(30), 1440, 0));
    }

    [Fact]
    public void Boundary_HonorsTheOffset()
    {
        // 05:00 UTC daily. Before it, today's; after it, tomorrow's.
        var offset = 300;
        Assert.Equal(
            new DateTime(2026, 7, 11, 5, 0, 0, DateTimeKind.Utc),
            NotificationService.NextDigestBoundary(new DateTime(2026, 7, 11, 4, 59, 0, DateTimeKind.Utc), 1440, offset));
        Assert.Equal(
            new DateTime(2026, 7, 12, 5, 0, 0, DateTimeKind.Utc),
            NotificationService.NextDigestBoundary(new DateTime(2026, 7, 11, 5, 0, 0, DateTimeKind.Utc), 1440, offset));
    }

    [Fact]
    public void Boundary_SkipsPastWindowsMissedWhileTheHostWasDown()
    {
        // Six-hourly, resuming three days late: the next boundary is the coming one, not a backlog of them.
        var next = NotificationService.NextDigestBoundary(Now, 360, 0);
        Assert.Equal(new DateTime(2026, 7, 11, 18, 0, 0, DateTimeKind.Utc), next);
        Assert.True(next > Now);
    }

    [Fact]
    public void Boundary_IsAlwaysInTheFuture_ForEveryMinuteOfADay()
    {
        var midnight = new DateTime(2026, 7, 11, 0, 0, 0, DateTimeKind.Utc);
        for (var minute = 0; minute < 1440; minute++)
        {
            var instant = midnight.AddMinutes(minute).AddSeconds(37);
            Assert.True(NotificationService.NextDigestBoundary(instant, 360, 120) > instant);
        }
    }

    // ---- composition ---------------------------------------------------------------------------------------

    [Fact]
    public void EmptyWindow_ComposesAnAllClearReport_NamingThePeriod()
    {
        var window = new NotificationWindow(Now.AddHours(-24), Now);
        var message = NotificationComposer.ComposeReport(new NotificationComposition(
            NotificationChannels.Email, NotificationModes.Digest, [], MorePending: false,
            "https://sqlflow.example.com", Now, window));

        Assert.Equal("SQLFlow digest: no failures", message.Subject);
        Assert.Contains("No failures between 2026-07-10 12:34 UTC and 2026-07-11 12:34 UTC.", message.TextBody,
            StringComparison.Ordinal);

        // A report is rendered for every channel at once: the one stored artifact is deliverable either way.
        Assert.NotNull(message.HtmlBody);
        Assert.NotNull(message.SlackBlocksJson);
        Assert.Contains("succeeded or is still running", message.HtmlBody, StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Array, JsonDocument.Parse(message.SlackBlocksJson).RootElement.ValueKind);
    }

    [Fact]
    public void PopulatedWindow_CountsTheWindow_NotOnlyTheEventSpan()
    {
        // The events sit in the middle of a 24 hour window; the header must state the window asked for, so an
        // operator can tell "quiet since lunch" from "we only looked at lunchtime".
        var window = new NotificationWindow(Now.AddHours(-24), Now);
        var events = new[] { Event(1), Event(2) };
        var message = NotificationComposer.ComposeReport(new NotificationComposition(
            NotificationChannels.Email, NotificationModes.Digest, events, MorePending: false,
            "https://sqlflow.example.com", Now, window));

        Assert.Contains("2 events between 2026-07-10 12:34 UTC and 2026-07-11 12:34 UTC.", message.TextBody,
            StringComparison.Ordinal);
        Assert.Contains("orders-load", message.TextBody, StringComparison.Ordinal);

        // Both destinations are offered, and both name the most recent occurrence: the run that failed, and the
        // flow behind it.
        var group = Assert.Single(NotificationComposer.Group(events));
        Assert.Equal(events[1].RunId, group.LastRunId);
        Assert.Contains($"/runs/{group.LastRunId}", message.TextBody, StringComparison.Ordinal);
        Assert.Contains($"/pipelines/{group.PipelineId}", message.TextBody, StringComparison.Ordinal);
        Assert.NotNull(message.SlackBlocksJson);
        Assert.Contains("Open flow", message.SlackBlocksJson, StringComparison.Ordinal);
        Assert.NotNull(message.HtmlBody);
        Assert.Contains("Open flow", message.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void SubscriptionMessages_KeepTheirOwnHeader_AndOnlyTheirChannelBody()
    {
        // No window: a subscription's message covers "since the last one", which its events describe themselves.
        var message = NotificationComposer.Compose(new NotificationComposition(
            NotificationChannels.Slack, NotificationModes.Immediate, [Event(1)], MorePending: false, null, Now));

        Assert.StartsWith("SQLFlow: ", message.Subject, StringComparison.Ordinal);
        Assert.Null(message.HtmlBody);
        Assert.NotNull(message.SlackBlocksJson);
    }

    // ---- the persisted grouping ----------------------------------------------------------------------------

    [Fact]
    public void Groups_RoundTrip_AndCarryWhatTheTableShows()
    {
        var events = new List<CatalogNotificationEvent>
        {
            Event(1, flow: "orders-load", error: "Timeout expired."),
            Event(2, flow: "orders-load", error: "The target table vanished."),
            Event(3, kind: NotificationEventKinds.AssertionFailed, flow: "customers-load", error: null),
        };

        var json = NotificationDigestGroups.Serialize(NotificationComposer.Group(events));
        var groups = NotificationDigestGroups.Deserialize(json);

        Assert.Equal(2, groups.Count);
        var failed = Assert.Single(groups, g => g.EventKind == NotificationEventKinds.RunFailed);
        Assert.Equal("orders-load", failed.FlowName);
        Assert.Equal(2, failed.Count);
        // The most recent occurrence is the one whose error is worth reading.
        Assert.Equal("The target table vanished.", failed.LastError);
        Assert.Equal(events[1].RunId, failed.LastRunId);
        // The flow id rides along, so the digest can link to the flow and not only to the run that failed.
        Assert.Equal(events[1].PipelineId, failed.PipelineId);

        var assertions = Assert.Single(groups, g => g.EventKind == NotificationEventKinds.AssertionFailed);
        Assert.Null(assertions.LastError);
        Assert.Equal("customers-load", assertions.FlowName);
    }

    [Fact]
    public void Groups_AreCapped_AndErrorsExcerpted()
    {
        var events = Enumerable.Range(1, NotificationDigestGroups.MaxGroups + 50)
            .Select(i => Event(i, flow: $"flow-{i}", error: new string('x', 5000)))
            .ToList();

        var groups = NotificationDigestGroups.Deserialize(
            NotificationDigestGroups.Serialize(NotificationComposer.Group(events)));

        Assert.Equal(NotificationDigestGroups.MaxGroups, groups.Count);
        // One enormous engine error must not be able to bloat the stored row.
        Assert.All(groups, g => Assert.True(g.LastError is { Length: < 400 }));
    }

    [Fact]
    public void Groups_OfAnUnreadableColumn_ReadAsNone()
    {
        // A digest's own counts and bodies still tell the reader everything; the table simply has no rows.
        Assert.Empty(NotificationDigestGroups.Deserialize(null));
        Assert.Empty(NotificationDigestGroups.Deserialize(string.Empty));
        Assert.Empty(NotificationDigestGroups.Deserialize("not json"));
    }
}

/// <summary>
/// The estate digest against the real catalog: the exactly-once window claim that lets several control-plane
/// replicas share one digest clock, the cursor that makes a scheduled digest lossless, the on-demand digest that
/// must never disturb it, and retention. The assembly runs serially and every test restores the single shared
/// state row it touched.
/// </summary>
[Trait("Category", "Integration")]
public sealed class NotificationDigestStoreTests
{

    [SkippableFact]
    public async Task ScheduledDigest_AdvancesTheCursor_SoTheNextWindowStartsWhereThisOneEnded()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var db = CatalogDatabase.Create(cs);
        var run = NewFailedRun();

        try
        {
            db.Runs.Add(run);
            await db.SaveChangesAsync();
            await NotificationStore.DetectAsync(db, DateTime.UtcNow, TimeSpan.FromMinutes(30));
            var evt = await db.NotificationEvents.AsNoTracking().FirstAsync(e => e.RunId == run.RunId);

            var now = DateTime.UtcNow.AddMinutes(1);
            var first = await NotificationDigestGenerator.GenerateScheduledAsync(
                db, afterEventId: evt.Id - 1, periodStartUtc: now.AddHours(-1), nowUtc: now,
                guiBaseUrl: "https://sqlflow.example.com");

            Assert.Equal(NotificationDigestOrigins.Scheduled, first.Origin);
            Assert.True(first.EventCount >= 1);
            Assert.True(first.LastEventId >= evt.Id);
            Assert.Contains(run.FlowName, first.TextBody, StringComparison.Ordinal);
            Assert.NotEqual("[]", first.GroupsJson);

            var watermark = await NotificationStore.GetWatermarkAsync(db);
            Assert.NotNull(watermark);
            Assert.Equal(first.LastEventId, watermark.DigestCursorEventId);

            // The same window again, now starting from the advanced cursor: nothing is reported twice.
            var second = await NotificationDigestGenerator.GenerateScheduledAsync(
                db, watermark.DigestCursorEventId, now, now.AddMinutes(1), null);
            Assert.Equal(0, second.EventCount);
            Assert.Equal("SQLFlow digest: no failures", second.Subject);

            // An empty window leaves the cursor exactly where it was: there was nothing to skip past.
            var after = await NotificationStore.GetWatermarkAsync(db);
            Assert.NotNull(after);
            Assert.Equal(first.LastEventId, after.DigestCursorEventId);

            await CleanupDigestsAsync(db, first.Id, second.Id);
        }
        finally
        {
            await ResetDigestStateAsync(db);
            await db.NotificationEvents.Where(e => e.RunId == run.RunId).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RunId == run.RunId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task ManualDigest_LeavesTheScheduledCursorAlone()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var db = CatalogDatabase.Create(cs);
        var userId = Guid.NewGuid();
        var before = (await NotificationStore.GetWatermarkAsync(db))?.DigestCursorEventId ?? 0;
        var now = DateTime.UtcNow;

        // A past day, reported on long after it ended: the period and the generation stamp are separate.
        var day = now.Date.AddDays(-3);
        var digest = await NotificationDigestGenerator.GenerateManualAsync(
            db, day, day.AddDays(1), now, userId, guiBaseUrl: null);
        try
        {
            Assert.Equal(NotificationDigestOrigins.Manual, digest.Origin);
            Assert.Equal(userId, digest.GeneratedByUserId);
            Assert.Equal(day, digest.PeriodStartUtc);
            Assert.Equal(day.AddDays(1), digest.PeriodEndUtc);
            Assert.Equal(now, digest.GeneratedUtc);

            var after = (await NotificationStore.GetWatermarkAsync(db))?.DigestCursorEventId ?? 0;
            Assert.Equal(before, after);
        }
        finally
        {
            await CleanupDigestsAsync(db, digest.Id);
        }
    }

    [SkippableFact]
    public async Task DigestWindow_IsClaimedByExactlyOneReplica()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var db = CatalogDatabase.Create(cs);

        try
        {
            // Detection owns the creation of the single state row; a digest tick before it does nothing.
            await NotificationStore.DetectAsync(db, DateTime.UtcNow, TimeSpan.FromMinutes(30));
            var watermark = await NotificationStore.GetWatermarkAsync(db);
            Assert.NotNull(watermark);

            var now = DateTime.UtcNow;
            var due = now.AddHours(1);
            var observed = watermark.DigestDueUtc;
            Assert.True(await NotificationStore.TryClaimDigestWindowAsync(db, observed, due, now));
            // The replica that observed the same pre-claim value must lose, whatever it wanted to set.
            Assert.False(await NotificationStore.TryClaimDigestWindowAsync(db, observed, due.AddHours(1), now));

            var claimed = await NotificationStore.GetWatermarkAsync(db);
            Assert.NotNull(claimed);
            Assert.Equal(due, claimed.DigestDueUtc);
            // The claim stamps the next window's start, so consecutive periods chain without a gap.
            Assert.Equal(now, claimed.DigestPeriodStartUtc);
        }
        finally
        {
            await ResetDigestStateAsync(db);
        }
    }

    [SkippableFact]
    public async Task DigestTick_ArmsTheClockFirst_ThenGeneratesEachDueWindowOnce()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var db = CatalogDatabase.Create(cs);

        try
        {
            // Detection owns the single state row; the digest clock starts unarmed against it.
            await NotificationStore.DetectAsync(db, DateTime.UtcNow, TimeSpan.FromMinutes(30));
            await ResetDigestStateAsync(db);

            var start = DateTime.UtcNow;
            var armed = await NotificationService.RunDigestTickAsync(db, start, 60, 0, null);
            Assert.Equal(DigestTickOutcome.Armed, armed.Outcome);
            // Arming never generates: the estate's history before this instant was never part of a window.
            Assert.Null(armed.Digest);
            Assert.True(armed.NextDueUtc > start);

            // Inside the window the tick stands down.
            var idle = await NotificationService.RunDigestTickAsync(db, start.AddMinutes(1), 60, 0, null);
            Assert.Equal(DigestTickOutcome.Idle, idle.Outcome);
            Assert.Null(idle.Digest);

            var generated = await NotificationService.RunDigestTickAsync(db, armed.NextDueUtc.AddSeconds(1), 60, 0, null);
            Assert.Equal(DigestTickOutcome.Generated, generated.Outcome);
            Assert.NotNull(generated.Digest);
            Assert.Equal(NotificationDigestOrigins.Scheduled, generated.Digest.Origin);
            Assert.Null(generated.Digest.GeneratedByUserId);
            // The period runs from the instant the previous claim opened it, so windows chain without a gap.
            Assert.True((generated.Digest.PeriodStartUtc - start).Duration() < TimeSpan.FromSeconds(2));

            // The window it just claimed is not generated a second time.
            var after = await NotificationService.RunDigestTickAsync(db, armed.NextDueUtc.AddSeconds(2), 60, 0, null);
            Assert.Equal(DigestTickOutcome.Idle, after.Outcome);

            await CleanupDigestsAsync(db, generated.Digest.Id);
        }
        finally
        {
            await ResetDigestStateAsync(db);
        }
    }

    [SkippableFact]
    public async Task Retention_PrunesAgedDigests()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var db = CatalogDatabase.Create(cs);
        var ancient = DateTime.UtcNow.AddDays(-400);
        var aged = await NotificationDigestGenerator.GenerateManualAsync(
            db, ancient.AddHours(-1), ancient, ancient, Guid.NewGuid(), null);
        var fresh = await NotificationDigestGenerator.GenerateManualAsync(
            db, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow, DateTime.UtcNow, Guid.NewGuid(), null);

        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-365);
            var (_, _, digests) = await NotificationStore.PurgeExpiredAsync(db, cutoff, cutoff, cutoff);

            Assert.True(digests >= 1);
            Assert.Null(await NotificationStore.GetDigestAsync(db, aged.Id));
            Assert.NotNull(await NotificationStore.GetDigestAsync(db, fresh.Id));
        }
        finally
        {
            await CleanupDigestsAsync(db, aged.Id, fresh.Id);
        }
    }

    [SkippableFact]
    public async Task DigestList_ReadsNewestFirst_WithoutTheBodies()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var db = CatalogDatabase.Create(cs);
        var now = DateTime.UtcNow;
        var older = await NotificationDigestGenerator.GenerateManualAsync(
            db, now.AddHours(-2), now.AddMinutes(-5), now.AddMinutes(-5), Guid.NewGuid(), null);
        var newer = await NotificationDigestGenerator.GenerateManualAsync(
            db, now.AddHours(-1), now, now, Guid.NewGuid(), null);

        try
        {
            var list = await NotificationStore.ListDigestsAsync(db, 50);
            var olderIndex = IndexOf(list, older.Id);
            var newerIndex = IndexOf(list, newer.Id);
            Assert.True(newerIndex >= 0 && olderIndex > newerIndex, "the newest digest must lead the list");
            Assert.Equal(newer.Subject, list[newerIndex].Subject);
        }
        finally
        {
            await CleanupDigestsAsync(db, older.Id, newer.Id);
        }
    }

    private static int IndexOf(IReadOnlyList<NotificationDigestSummary> list, Guid id)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private static CatalogRun NewFailedRun() => new()
    {
        RunId = Guid.CreateVersion7(),
        PipelineId = Guid.NewGuid(),
        RepoId = Guid.NewGuid(),
        FlowName = $"digest-test-{Guid.NewGuid():N}",
        FlowKind = "ing",
        Status = RunStatuses.Failed,
        Success = false,
        Error = "the target table vanished",
        EndUtc = DateTime.UtcNow,
        WrittenUtc = DateTime.UtcNow,
    };

    private static async Task CleanupDigestsAsync(CatalogDbContext db, params Guid[] ids)
        => await db.NotificationDigests.Where(d => ids.Contains(d.Id)).ExecuteDeleteAsync();

    /// <summary>Puts the shared single-row digest clock back to "unarmed", so a test that claimed a window does
    /// not leave a due instant behind for the next test's host to act on.</summary>
    private static async Task ResetDigestStateAsync(CatalogDbContext db)
        => await db.NotificationWatermarks
            .Where(w => w.Id == CatalogNotificationWatermark.WellKnownId)
            .ExecuteUpdateAsync(w => w
                .SetProperty(x => x.DigestDueUtc, (DateTime?)null)
                .SetProperty(x => x.DigestPeriodStartUtc, (DateTime?)null)
                .SetProperty(x => x.DigestCursorEventId, 0L));
}
