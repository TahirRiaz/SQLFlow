using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.Core.Secrets;

namespace SqlFlow.ControlPlane.Background;

/// <summary>
/// Scans the catalog for due schedules and fires them by enqueuing a run onto the durable queue (the exact path a
/// manual trigger takes, so a scheduled run is in no way special). A schedule is fired by atomically advancing its
/// next-fire time, so when more than one control-plane node runs this service the compare-and-swap guarantees each
/// occurrence is enqueued exactly once. Missed occurrences (the host was down) are not backfilled: the next fire is
/// computed strictly after now, so a schedule fires once and resumes its cadence.
/// </summary>
/// <remarks>
/// Robustness: a bad cron / time zone on one schedule is logged and that schedule is parked (its next fire is
/// cleared) rather than re-scanned forever or stopping the loop; a tick error (a transient database outage) is
/// logged and retried next tick; an inactive or removed pipeline is skipped, not enqueued. Due schedules fire
/// with bounded concurrency, each on its own scope (and so its own DbContext); one schedule's failure is logged
/// and never stops the others. All diagnostics are secret-redacted.
/// </remarks>
public sealed partial class SchedulerService : BackgroundService
{
    private const int MaxPerTick = 200;

    // Each fire costs several catalog round trips (claim, pipeline check, enqueue, last-run stamp), so a burst of
    // due schedules (a shared top-of-the-hour cron) is fired concurrently instead of serializing all of it; the
    // claim's compare-and-swap already makes concurrent firing race-safe. Eight keeps the tick fast without
    // stampeding the catalog.
    private const int MaxConcurrentFires = 8;

    private readonly IServiceProvider _services;
    private readonly IRunDispatcher _dispatcher;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _pollInterval;
    private readonly ILogger<SchedulerService> _logger;

    public SchedulerService(
        IServiceProvider services,
        IRunDispatcher dispatcher,
        TimeProvider clock,
        IOptions<ControlPlaneOptions> options,
        ILogger<SchedulerService> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _dispatcher = dispatcher;
        _clock = clock;
        _pollInterval = TimeSpan.FromSeconds(Math.Max(1, options.Value.Scheduler.PollSeconds));
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                // Host teardown has disposed the DI container and the loggers out from under this tick. No work
                // remains and logging would itself throw (the disposed Windows EventLog provider), so leave the loop
                // quietly instead of escalating shutdown noise into a "BackgroundService failed".
                break;
            }
            catch (Exception ex)
            {
                LogTickError(SecretHygiene.RedactedMessage(ex));
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        IReadOnlyList<CatalogSchedule> due;
        IReadOnlyList<CatalogSchedule> chained;
        await using (var scope = _services.CreateAsyncScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            due = await ScheduleStore.ListDueAsync(catalog, now, MaxPerTick, ct).ConfigureAwait(false);

            // The second driver: schedules with no cadence of their own, waiting on a parent's fire to finish. Scanned
            // every tick alongside the clock scan, so a chain advances one link per tick as each parent completes.
            chained = await ScheduleStore.ListChainedReadyAsync(catalog, MaxPerTick, ct).ConfigureAwait(false);
        }

        if (due.Count == 0 && chained.Count == 0)
        {
            return;
        }

        // Bounded fan-out: each fire runs on its own scope (a DbContext is not thread-safe), gated so a burst of
        // due schedules never opens more than MaxConcurrentFires catalog conversations at once. WhenAll observes
        // every task, so the gate is fully released before it is disposed.
        using var gate = new SemaphoreSlim(MaxConcurrentFires, MaxConcurrentFires);
        var fires = due.Select(schedule => FireGuardedAsync(schedule, now, gate, ct))
            .Concat(chained.Select(schedule => FireChainedGuardedAsync(schedule, now, gate, ct)));
        await Task.WhenAll(fires).ConfigureAwait(false);
    }

    /// <summary>Fires one ready chained schedule behind the same concurrency gate and isolation as a clock fire.</summary>
    private async Task FireChainedGuardedAsync(CatalogSchedule schedule, DateTime now, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var scope = _services.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await FireChainedAsync(catalog, schedule, now, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // shutdown mid-tick; ExecuteAsync observes the cancellation and stops cleanly
        }
        catch (Exception ex)
        {
            LogFireError(schedule.Id, schedule.Name, SecretHygiene.RedactedMessage(ex));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Fires a chained schedule behind its completed parents. The claim stamps the newest parent fire being consumed
    /// rather than advancing a next-fire, which is what makes one round of parent fires trigger the child exactly once
    /// however many nodes or ticks observe the same completed set.
    /// <para>
    /// Parents older than the schedule's freshness window are recorded and logged as a warning, and the fire proceeds
    /// regardless. See <c>ScheduleSpec.ParentFreshnessHours</c>: a fan-in step is normally a rebuild that corrects
    /// itself next cycle, so a stale parent costs one cycle of accuracy, while blocking would stop the step updating
    /// for as long as the quiet parent stays quiet, with nothing failing anywhere to show it.
    /// </para>
    /// </summary>
    private async Task FireChainedAsync(CatalogDbContext catalog, CatalogSchedule schedule, DateTime now, CancellationToken ct)
    {
        // Re-read the parents' fire state on this scope: the scan that selected this child ran on another scope, and
        // the value stamped must be the one readiness was judged against.
        var (newestFire, staleParents, parentLabel) = await ScheduleStore.GetParentFireStateAsync(
            catalog, schedule.Id, schedule.ParentFreshnessHours, now, ct).ConfigureAwait(false);
        if (newestFire is not { } parentFireUtc || parentFireUtc == schedule.LastParentFireUtc)
        {
            return; // the parents were re-declared, or another node already consumed this fire
        }

        var won = await ScheduleStore.TryClaimChainedFireAsync(
            catalog, schedule.Id, schedule.LastParentFireUtc, parentFireUtc, now, staleParents, ct).ConfigureAwait(false);
        if (!won)
        {
            return;
        }

        if (staleParents is not null)
        {
            LogStaleParents(schedule.Id, schedule.Name, staleParents, schedule.ParentFreshnessHours);
        }

        var fire = await ScheduleFire.EnqueueAsync(catalog, _dispatcher, schedule, now, ct).ConfigureAwait(false);
        switch (fire.Outcome)
        {
            case ScheduleFire.Outcome.ScopeEmpty:
                LogScopeEmpty(schedule.Id, schedule.Name);
                break;
            case ScheduleFire.Outcome.Enqueued:
                LogChainedFired(schedule.Id, schedule.Name, parentLabel, fire.RunId);
                break;
            case ScheduleFire.Outcome.EnqueuedGroup:
                LogChainedFiredGroup(schedule.Id, fire.MemberCount, schedule.Name, parentLabel, fire.GroupId ?? Guid.Empty);
                break;
        }
    }

    /// <summary>Fires one due schedule behind the concurrency gate, on its own scope. A failure (a transient
    /// database outage, a bad pipeline row) is logged and confined to this schedule so the rest of the tick's due
    /// set still fires; only cancellation propagates, and the unclaimed occurrences simply re-scan next tick.</summary>
    private async Task FireGuardedAsync(CatalogSchedule schedule, DateTime now, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var scope = _services.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await FireAsync(catalog, schedule, now, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // shutdown mid-tick; ExecuteAsync observes the cancellation and stops cleanly
        }
        catch (Exception ex)
        {
            LogFireError(schedule.Id, schedule.Name, SecretHygiene.RedactedMessage(ex));
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task FireAsync(CatalogDbContext catalog, CatalogSchedule schedule, DateTime now, CancellationToken ct)
    {
        if (schedule.NextFireUtc is not { } observed)
        {
            return; // not actually due (defensive against a concurrent change)
        }

        // Where the next fire is computed from decides catch-up. Without catchup the next fire is strictly after now,
        // so a fire the host missed is skipped and the schedule resumes its cadence. With catchup the next fire is
        // computed from the missed occurrence itself, so an overdue schedule advances one occurrence per tick and
        // fires each missed window in turn until it is current again.
        var advanceFrom = schedule.Catchup ? observed : now;
        var next = ScheduleClock.NextFire(schedule.Cron, schedule.IntervalSeconds, schedule.Timezone, advanceFrom);

        // Claim this occurrence by advancing the next-fire from the value we observed. Only the winner proceeds.
        var won = await ScheduleStore.TryClaimFireAsync(catalog, schedule.Id, observed, next, now, ct).ConfigureAwait(false);
        if (!won)
        {
            return;
        }

        if (next is null)
        {
            // No computable next fire (a malformed cron/time zone, or a cron with no further occurrence): the claim
            // above advanced the schedule to null, so it is parked and not scanned again, and this occurrence is not
            // enqueued.
            LogParked(schedule.Id, schedule.Name);
            return;
        }

        // Enqueue through the shared fire path (the same one the manual run-now endpoint uses): it expands the
        // schedule's member set, enqueues it in wave order, and stamps the last run/group. A member declaring
        // mode: manual is excluded there, since a schedule is automatic dispatch and that flag reserves a flow for a
        // direct trigger; the occurrence's next fire has already advanced, so the cadence simply moves on.
        var fire = await ScheduleFire.EnqueueAsync(catalog, _dispatcher, schedule, now, ct).ConfigureAwait(false);
        switch (fire.Outcome)
        {
            case ScheduleFire.Outcome.ScopeEmpty:
                LogScopeEmpty(schedule.Id, schedule.Name);
                break;
            case ScheduleFire.Outcome.Enqueued:
                LogFired(schedule.Id, schedule.Name, fire.RunId);
                break;
            case ScheduleFire.Outcome.EnqueuedGroup:
                LogFiredGroup(schedule.Id, fire.MemberCount, schedule.Name, fire.GroupId ?? Guid.Empty);
                break;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Schedule {ScheduleId} ('{ScheduleName}') fired: enqueued run {RunId} for its single member.")]
    private partial void LogFired(Guid scheduleId, string scheduleName, Guid runId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Schedule {ScheduleId} ('{ScheduleName}') fired: enqueued its {MemberCount} members as wave-ordered run group {GroupId}.")]
    private partial void LogFiredGroup(Guid scheduleId, int memberCount, string scheduleName, Guid groupId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Schedule {ScheduleId} ('{ScheduleName}') fired behind '{ParentName}': enqueued run {RunId} for its single member.")]
    private partial void LogChainedFired(Guid scheduleId, string scheduleName, string parentName, Guid runId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Schedule {ScheduleId} ('{ScheduleName}') fired behind '{ParentName}': enqueued its {MemberCount} members as wave-ordered run group {GroupId}.")]
    private partial void LogChainedFiredGroup(Guid scheduleId, int memberCount, string scheduleName, string parentName, Guid groupId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Schedule {ScheduleId} ('{ScheduleName}') resolved to no runnable flow: nothing joins it, or every member is deactivated or mode: manual. Nothing enqueued this occurrence.")]
    private partial void LogScopeEmpty(Guid scheduleId, string scheduleName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Schedule {ScheduleId} ('{ScheduleName}') fired with stale parents: {StaleParents} last ran more than {FreshnessHours}h ago. The fire proceeded; whatever it rebuilds is fed by those parents' previous data until they run again.")]
    private partial void LogStaleParents(Guid scheduleId, string scheduleName, string staleParents, int freshnessHours);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Schedule {ScheduleId} ('{ScheduleName}') has no computable next fire (invalid cron/timezone or exhausted) and was parked.")]
    private partial void LogParked(Guid scheduleId, string scheduleName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduler tick error: {Error}")]
    private partial void LogTickError(string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Schedule {ScheduleId} ('{ScheduleName}') failed to fire: {Error}")]
    private partial void LogFireError(Guid scheduleId, string scheduleName, string error);
}
