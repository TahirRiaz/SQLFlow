using SqlFlow.Catalog;
using SqlFlow.Dispatch;

namespace SqlFlow.ControlPlane.Background;

/// <summary>
/// The API's seam onto the run queue: enqueue and cancel runs and compute tasks. Every operation is ledger-first
/// (the catalog row is written with the caller's context, so a run is visible to the read API and durable before
/// anything else happens) and then notifies the in-memory dispatcher so placement happens at once. On a replica
/// whose dispatcher is passive the notify is a no-op and the owner's reconcile picks the row up within its
/// interval, so an API replica never needs to know which replica dispatches.
/// </summary>
public interface IRunDispatcher
{
    /// <summary>Enqueues the run described by <paramref name="request"/> (references only - repo, flow, optional
    /// pool and commit SHA) using the caller's catalog context, and returns the minted run id so the caller can
    /// point a client at <c>GET /api/v1/runs/{runId}</c>.</summary>
    Task<Guid> EnqueueAsync(CatalogDbContext catalog, RunEnqueueRequest request, CancellationToken ct = default);

    /// <summary>Enqueues a whole run group (a Node or Batch execution) as one wave-ordered set and returns the group
    /// id with its member run ids, so the caller can point a client at the group view.</summary>
    Task<RunGroupEnqueueResult> EnqueueGroupAsync(
        CatalogDbContext catalog, RunGroupEnqueueRequest request, CancellationToken ct = default);

    /// <summary>Cancels a run: a still-queued run is dequeued outright; a run already executing has a durable cancel
    /// request stamped, which the dispatcher relays to its node on the node's next poll (woken at once). Returns the
    /// precise <see cref="CancelOutcome"/> so the endpoint can answer 200 / 202 / 404 / 409.</summary>
    Task<CancelOutcome> CancelAsync(CatalogDbContext catalog, Guid runId, CancellationToken ct = default);

    /// <summary>Cancels a whole run group: every queued member is dequeued and every running member gets a durable
    /// cancel request. Returns whether the group existed and how many members were affected.</summary>
    Task<GroupCancelResult> CancelGroupAsync(CatalogDbContext catalog, Guid groupId, CancellationToken ct = default);

    /// <summary>Enqueues an ad-hoc datasource compute task (references only; the executing node resolves the
    /// credentials) and returns the minted task id, so the caller can point a client at
    /// <c>GET /api/v1/datasources/tasks/{taskId}</c>.</summary>
    Task<Guid> EnqueueComputeTaskAsync(CatalogDbContext catalog, ComputeTaskEnqueueRequest request, CancellationToken ct = default);

    /// <summary>Cancels a compute task: a still-queued task is dequeued outright; a running task gets a durable
    /// cancel request relayed to its node.</summary>
    Task<CancelOutcome> CancelComputeTaskAsync(CatalogDbContext catalog, Guid taskId, CancellationToken ct = default);
}

/// <summary>The one implementation: journals through the stores, then tells the local <see cref="Dispatcher"/>.</summary>
public sealed class InProcessRunDispatcher : IRunDispatcher
{
    private readonly Dispatcher _dispatcher;
    private readonly TimeProvider _clock;

    public InProcessRunDispatcher(Dispatcher dispatcher, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(clock);
        _dispatcher = dispatcher;
        _clock = clock;
    }

    public async Task<Guid> EnqueueAsync(CatalogDbContext catalog, RunEnqueueRequest request, CancellationToken ct = default)
    {
        var result = await RunQueueStore
            .EnqueueAsync(catalog, request, _clock.GetUtcNow().UtcDateTime, ct)
            .ConfigureAwait(false);
        _dispatcher.NotifyRunsEnqueued([result.Placement]);
        return result.RunId;
    }

    public async Task<RunGroupEnqueueResult> EnqueueGroupAsync(
        CatalogDbContext catalog, RunGroupEnqueueRequest request, CancellationToken ct = default)
    {
        var result = await RunQueueStore
            .EnqueueGroupAsync(catalog, request, _clock.GetUtcNow().UtcDateTime, ct)
            .ConfigureAwait(false);
        _dispatcher.NotifyRunsEnqueued(result.Placements);
        return result;
    }

    public async Task<CancelOutcome> CancelAsync(CatalogDbContext catalog, Guid runId, CancellationToken ct = default)
    {
        var outcome = await RunQueueStore
            .CancelAsync(catalog, runId, _clock.GetUtcNow().UtcDateTime, ct)
            .ConfigureAwait(false);
        if (outcome is CancelOutcome.Cancelled or CancelOutcome.CancelRequested)
        {
            _dispatcher.NotifyRunCancelled(runId);
        }

        return outcome;
    }

    public async Task<GroupCancelResult> CancelGroupAsync(
        CatalogDbContext catalog, Guid groupId, CancellationToken ct = default)
    {
        var result = await RunQueueStore
            .CancelGroupAsync(catalog, groupId, _clock.GetUtcNow().UtcDateTime, ct)
            .ConfigureAwait(false);
        if (result.Found)
        {
            _dispatcher.NotifyGroupCancelled(groupId);
        }

        return result;
    }

    public async Task<Guid> EnqueueComputeTaskAsync(
        CatalogDbContext catalog, ComputeTaskEnqueueRequest request, CancellationToken ct = default)
    {
        var result = await ComputeTaskStore
            .EnqueueAsync(catalog, request, _clock.GetUtcNow().UtcDateTime, ct)
            .ConfigureAwait(false);
        _dispatcher.NotifyTaskEnqueued(result.Placement);
        return result.TaskId;
    }

    public async Task<CancelOutcome> CancelComputeTaskAsync(
        CatalogDbContext catalog, Guid taskId, CancellationToken ct = default)
    {
        var outcome = await ComputeTaskStore
            .CancelAsync(catalog, taskId, _clock.GetUtcNow().UtcDateTime, ct)
            .ConfigureAwait(false);
        if (outcome is CancelOutcome.Cancelled or CancelOutcome.CancelRequested)
        {
            _dispatcher.NotifyTaskCancelled(taskId);
        }

        return outcome;
    }
}
