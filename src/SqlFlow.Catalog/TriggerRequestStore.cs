using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Catalog;

/// <summary>
/// A request to make, checked by its caller: the repository and its source, who asks (an external trigger or a person),
/// the caller's own key for it, where it came from, the commit expected, whether to sync, the batches and chain every
/// fire takes, and the schedules to fire, in order.
/// </summary>
public sealed record NewTriggerRequest(
    string RepoName, Guid? RepoSourceId, Guid? TriggerId, string? TriggerName, string RequestedBy, string? RequestKey,
    string? Origin, string? OriginUrl, string? ExpectedCommit, bool Sync, IReadOnlyList<string> Batch, bool Chain,
    IReadOnlyList<string> Schedules);

/// <summary>A request and its schedules, in the order the request named them.</summary>
public sealed record TriggerRequestRecord(CatalogTriggerRequest Request, IReadOnlyList<CatalogTriggerRequestSchedule> Schedules);

/// <summary>What narrows a request list: the trigger, the repository, the state; null leaves a filter off.</summary>
public sealed record TriggerRequestQuery(Guid? TriggerId = null, string? RepoName = null, string? State = null);

/// <summary>How asking to cancel a request went.</summary>
public enum TriggerRequestCancel
{
    /// <summary>The cancel is recorded; the processor stops the request and cancels its runs.</summary>
    Requested,

    NotFound,

    /// <summary>The request had already ended.</summary>
    AlreadyEnded,
}

/// <summary>
/// Persistence for trigger requests: make one (answering with the one already made when the caller's key repeats), read
/// one with its schedules, list them, find the ones still running, lease one to a single control-plane replica while it
/// works on it, ask to cancel one, and keep the link from each run a request fired back to it. The processor that moves a
/// request along lives in the control plane; this only holds the record.
/// </summary>
public static class TriggerRequestStore
{
    /// <summary>The most schedules one request may name.</summary>
    public const int MaxSchedules = 50;

    /// <summary>The most batches one request may narrow its fires to.</summary>
    public const int MaxBatches = 20;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The batches a request stores, as the JSON array <see cref="CatalogTriggerRequest.Batch"/> holds.</summary>
    public static string? SerializeBatch(IReadOnlyList<string> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return batch.Count == 0 ? null : JsonSerializer.Serialize(batch, Json);
    }

    /// <summary>The batches of <see cref="CatalogTriggerRequest.Batch"/>; none for an empty or unreadable value.</summary>
    public static IReadOnlyList<string> ParseBatch(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, Json)?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Makes the request, with one schedule row per schedule it names, its trace's id, and the state it starts in
    /// (<see cref="TriggerRequestStates.Syncing"/>). When the caller gave a key it already made a request with, nothing is
    /// made and the existing request is returned with <c>Created</c> false, so a retried call or a re-run job is answered
    /// with the request it already made. The check and the insert share one serializable transaction, so two concurrent
    /// calls with the same key make one request.
    /// </summary>
    public static async Task<(TriggerRequestRecord Record, bool Created)> CreateAsync(
        CatalogDbContext catalog, NewTriggerRequest request, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RequestedBy);
        if (request.Schedules.Count > MaxSchedules)
        {
            throw new ArgumentException($"A request names at most {MaxSchedules} schedules.", nameof(request));
        }

        var key = string.IsNullOrWhiteSpace(request.RequestKey) ? null : request.RequestKey.Trim();
        var (id, created) = await CatalogTransaction.InSerializableAsync(catalog, async () =>
        {
            if (key is not null)
            {
                var existing = await catalog.TriggerRequests
                    .Where(r => r.RequestedBy == request.RequestedBy && r.RequestKey == key)
                    .Select(r => (Guid?)r.Id)
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);
                if (existing is { } existingId)
                {
                    return (existingId, false);
                }
            }

            var row = new CatalogTriggerRequest
            {
                Id = Guid.NewGuid(),
                RepoName = request.RepoName.Trim(),
                RepoSourceId = request.RepoSourceId,
                TriggerId = request.TriggerId,
                TriggerName = request.TriggerName,
                RequestedBy = request.RequestedBy.Trim(),
                RequestKey = key,
                Origin = Bound(request.Origin, 1000),
                OriginUrl = Bound(request.OriginUrl, 1024),
                ExpectedCommit = string.IsNullOrWhiteSpace(request.ExpectedCommit) ? null : request.ExpectedCommit.Trim().ToLowerInvariant(),
                Sync = request.Sync,
                Batch = SerializeBatch(request.Batch),
                Chain = request.Chain,
                State = TriggerRequestStates.Syncing,
                ActivityId = Guid.NewGuid(),
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc,
            };
            catalog.TriggerRequests.Add(row);
            var ordinal = 0;
            foreach (var schedule in request.Schedules)
            {
                catalog.TriggerRequestSchedules.Add(new CatalogTriggerRequestSchedule
                {
                    RequestId = row.Id,
                    Ordinal = ++ordinal,
                    ScheduleName = schedule.Trim(),
                    State = TriggerScheduleStates.Pending,
                });
            }

            return (row.Id, true);
        }, ct).ConfigureAwait(false);

        var record = await GetAsync(catalog, id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Trigger request {id} was not found right after it was made.");
        return (record, created);
    }

    /// <summary>The request <paramref name="id"/> with its schedules in order, or null.</summary>
    public static async Task<TriggerRequestRecord?> GetAsync(CatalogDbContext catalog, Guid id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var request = await catalog.TriggerRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct).ConfigureAwait(false);
        if (request is null)
        {
            return null;
        }

        var schedules = await catalog.TriggerRequestSchedules.AsNoTracking()
            .Where(s => s.RequestId == id).OrderBy(s => s.Ordinal)
            .ToListAsync(ct).ConfigureAwait(false);
        return new TriggerRequestRecord(request, schedules);
    }

    /// <summary>A page of requests, newest first, with each one's schedules, and how many match in all.</summary>
    public static async Task<(IReadOnlyList<TriggerRequestRecord> Items, long Total)> ListAsync(
        CatalogDbContext catalog, TriggerRequestQuery query, int page, int pageSize, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(query);
        var requests = catalog.TriggerRequests.AsNoTracking().AsQueryable();
        if (query.TriggerId is { } triggerId)
        {
            requests = requests.Where(r => r.TriggerId == triggerId);
        }

        if (!string.IsNullOrWhiteSpace(query.RepoName))
        {
            var repo = query.RepoName.Trim();
            requests = requests.Where(r => r.RepoName == repo);
        }

        if (!string.IsNullOrWhiteSpace(query.State))
        {
            var state = query.State.Trim().ToLowerInvariant();
            requests = requests.Where(r => r.State == state);
        }

        var total = await requests.LongCountAsync(ct).ConfigureAwait(false);
        var rows = await requests.OrderByDescending(r => r.CreatedUtc).ThenBy(r => r.Id)
            .Skip(Math.Max(0, page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);
        var ids = rows.Select(r => r.Id).ToList();
        var schedules = await catalog.TriggerRequestSchedules.AsNoTracking()
            .Where(s => ids.Contains(s.RequestId))
            .ToListAsync(ct).ConfigureAwait(false);
        var byRequest = schedules.GroupBy(s => s.RequestId).ToDictionary(g => g.Key, g => g.OrderBy(s => s.Ordinal).ToList());
        return (rows.Select(r => new TriggerRequestRecord(r, byRequest.GetValueOrDefault(r.Id) ?? [])).ToList(), total);
    }

    /// <summary>The ids of the requests still running, oldest first, at most <paramref name="max"/>.</summary>
    public static async Task<IReadOnlyList<Guid>> ListOpenAsync(CatalogDbContext catalog, int max, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return await catalog.TriggerRequests.AsNoTracking()
            .Where(r => r.CompletedUtc == null)
            .OrderBy(r => r.CreatedUtc)
            .Select(r => r.Id)
            .Take(Math.Clamp(max, 1, 1000))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Leases request <paramref name="id"/> to <paramref name="owner"/> until <paramref name="untilUtc"/>, when it is still
    /// running and no other replica holds it (or the other replica's lease has lapsed). True when the caller holds it now;
    /// one conditional update, so two replicas never work on one request at once.
    /// </summary>
    public static async Task<bool> TryLeaseAsync(
        CatalogDbContext catalog, Guid id, string owner, DateTime nowUtc, DateTime untilUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        var affected = await catalog.TriggerRequests
            .Where(r => r.Id == id && r.CompletedUtc == null
                        && (r.LeaseUntilUtc == null || r.LeaseUntilUtc < nowUtc || r.LeaseOwner == owner))
            .ExecuteUpdateAsync(r => r
                .SetProperty(x => x.LeaseOwner, owner)
                .SetProperty(x => x.LeaseUntilUtc, untilUtc), ct)
            .ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>Gives up <paramref name="owner"/>'s lease on request <paramref name="id"/>, so the next tick of any replica
    /// may take it at once.</summary>
    public static Task ReleaseLeaseAsync(CatalogDbContext catalog, Guid id, string owner, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        return catalog.TriggerRequests
            .Where(r => r.Id == id && r.LeaseOwner == owner)
            .ExecuteUpdateAsync(r => r
                .SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null), ct);
    }

    /// <summary>Asks to cancel request <paramref name="id"/>: recorded on the request, acted on by the processor's next
    /// tick, which stops waiting for the sync, fires nothing more and cancels the runs the request fired.</summary>
    public static async Task<TriggerRequestCancel> RequestCancelAsync(
        CatalogDbContext catalog, Guid id, string requestedBy, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);
        var affected = await catalog.TriggerRequests
            .Where(r => r.Id == id && r.CompletedUtc == null)
            .ExecuteUpdateAsync(r => r
                .SetProperty(x => x.CancelRequestedUtc, x => x.CancelRequestedUtc ?? nowUtc)
                .SetProperty(x => x.CancelRequestedBy, x => x.CancelRequestedBy ?? requestedBy)
                .SetProperty(x => x.UpdatedUtc, nowUtc), ct)
            .ConfigureAwait(false);
        if (affected > 0)
        {
            return TriggerRequestCancel.Requested;
        }

        return await catalog.TriggerRequests.AnyAsync(r => r.Id == id, ct).ConfigureAwait(false)
            ? TriggerRequestCancel.AlreadyEnded
            : TriggerRequestCancel.NotFound;
    }

    /// <summary>
    /// Ends request <paramref name="id"/> as failed with <paramref name="reason"/>, when it is still running: the way out for
    /// a request whose processing keeps failing. Its schedules that never fired are marked so; runs it fired keep running
    /// and stay linked to it. True when this call ended it.
    /// </summary>
    public static Task<bool> EndAsFailedAsync(CatalogDbContext catalog, Guid id, string reason, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var detail = Bound(reason, 2000)!;
        return CatalogTransaction.InSerializableAsync(catalog, async () =>
        {
            var ended = await catalog.TriggerRequests
                .Where(r => r.Id == id && r.CompletedUtc == null)
                .ExecuteUpdateAsync(r => r
                    .SetProperty(x => x.State, TriggerRequestStates.Failed)
                    .SetProperty(x => x.Detail, detail)
                    .SetProperty(x => x.CompletedUtc, nowUtc)
                    .SetProperty(x => x.UpdatedUtc, nowUtc), ct)
                .ConfigureAwait(false);
            if (ended == 0)
            {
                return false;
            }

            await catalog.TriggerRequestSchedules
                .Where(s => s.RequestId == id && s.State == TriggerScheduleStates.Pending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.State, TriggerScheduleStates.NotFired)
                    .SetProperty(x => x.Detail, "Not fired: the request stopped after repeated errors.")
                    .SetProperty(x => x.CompletedUtc, nowUtc), ct)
                .ConfigureAwait(false);
            await catalog.TriggerRequestSchedules
                .Where(s => s.RequestId == id && s.State == TriggerScheduleStates.Firing)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.State, TriggerScheduleStates.Failed)
                    .SetProperty(x => x.Detail, "The request stopped after repeated errors while firing this schedule; the fire may have queued runs, which the schedule's run history shows.")
                    .SetProperty(x => x.CompletedUtc, nowUtc), ct)
                .ConfigureAwait(false);
            await catalog.TriggerRequestSchedules
                .Where(s => s.RequestId == id && s.State == TriggerScheduleStates.Running)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.State, TriggerScheduleStates.Failed)
                    .SetProperty(x => x.Detail, "The request stopped after repeated errors before this schedule's runs ended; they keep running, and its run group shows how they end.")
                    .SetProperty(x => x.CompletedUtc, nowUtc), ct)
                .ConfigureAwait(false);
            return true;
        }, ct);
    }

    /// <summary>Links the runs a schedule's fire queued to request <paramref name="requestId"/>, each reported as queued.
    /// A link already there is kept as it is, so linking again after a restart adds only what is missing.</summary>
    public static async Task LinkRunsAsync(
        CatalogDbContext catalog, Guid requestId, int scheduleOrdinal, IReadOnlyList<(Guid RunId, string FlowName)> runs,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(runs);
        if (runs.Count == 0)
        {
            return;
        }

        var ids = runs.Select(r => r.RunId).ToList();
        var linked = await catalog.TriggerRequestRuns.AsNoTracking()
            .Where(r => r.RequestId == requestId && ids.Contains(r.RunId))
            .Select(r => r.RunId)
            .ToListAsync(ct).ConfigureAwait(false);
        var added = new List<CatalogTriggerRequestRun>();
        foreach (var (runId, flowName) in runs.Where(r => !linked.Contains(r.RunId)).DistinctBy(r => r.RunId))
        {
            var link = new CatalogTriggerRequestRun
            {
                RequestId = requestId,
                RunId = runId,
                ScheduleOrdinal = scheduleOrdinal,
                FlowName = flowName,
                ReportedStatus = RunStatuses.Queued,
            };
            catalog.TriggerRequestRuns.Add(link);
            added.Add(link);
        }

        await catalog.SaveChangesAsync(ct).ConfigureAwait(false);

        // Detach only the links, so a caller tracking its own rows on this context (the request it is moving along) keeps them.
        foreach (var link in added)
        {
            catalog.Entry(link).State = EntityState.Detached;
        }
    }

    /// <summary>The runs request <paramref name="requestId"/> fired, as linked.</summary>
    public static async Task<IReadOnlyList<CatalogTriggerRequestRun>> RunsOfAsync(
        CatalogDbContext catalog, Guid requestId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return await catalog.TriggerRequestRuns.AsNoTracking()
            .Where(r => r.RequestId == requestId)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records that the request's trace now reports run <paramref name="runId"/> as <paramref name="status"/>, when it last
    /// reported anything else. True when this call made the change, so whichever replica sees a run start or end writes
    /// that line exactly once.
    /// </summary>
    public static async Task<bool> TryMarkReportedAsync(
        CatalogDbContext catalog, Guid requestId, Guid runId, string status, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        var affected = await catalog.TriggerRequestRuns
            .Where(r => r.RequestId == requestId && r.RunId == runId && r.ReportedStatus != status)
            .ExecuteUpdateAsync(r => r.SetProperty(x => x.ReportedStatus, status), ct)
            .ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>The request that fired run <paramref name="runId"/>, or null when no request did.</summary>
    public static Task<Guid?> RequestOfRunAsync(CatalogDbContext catalog, Guid runId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.TriggerRequestRuns.AsNoTracking()
            .Where(r => r.RunId == runId)
            .Select(r => (Guid?)r.RequestId)
            .FirstOrDefaultAsync(ct);
    }

    private static string? Bound(string? value, int max)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > max ? trimmed[..max] : trimmed;
    }
}
