using Microsoft.EntityFrameworkCore;
using SqlFlow.Dispatch;

namespace SqlFlow.Catalog;

/// <summary>What to enqueue: the operation, the connection REFERENCE (never a secret), the validated payload
/// as JSON, optional pool routing, and the requester for audit. Bundled so the optional fields can never be
/// passed in the wrong order.</summary>
public sealed record ComputeTaskEnqueueRequest(
    string Operation, string SourceRef, string? ProviderKind, string ArgumentsJson,
    string? TargetPool = null, string? RequestedBy = null);

/// <summary>The outcome of enqueuing a compute task: its new id and its placement row for the dispatcher.</summary>
public sealed record ComputeTaskEnqueueResult(Guid TaskId, DispatchTask Placement);

/// <summary>
/// The compute-task journal: the <see cref="CatalogComputeTask"/> table records every ad-hoc inspection from
/// <c>queued</c> through terminal, so a requested task survives a host restart and is visible to the read API from
/// the moment it is queued. Placement is the dispatcher's; this store journals its decisions with plain conditional
/// updates fenced on the holding node, exactly as <see cref="RunQueueStore"/> does for runs. Stateless.
/// </summary>
public static class ComputeTaskStore
{
    /// <summary>Enqueues a task: inserts a <c>queued</c> row and returns its newly minted (time-ordered) id and
    /// placement, so the caller can point the client at <c>GET /datasources/tasks/{id}</c> immediately.</summary>
    public static async Task<ComputeTaskEnqueueResult> EnqueueAsync(
        CatalogDbContext catalog, ComputeTaskEnqueueRequest request, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ArgumentsJson);

        var taskId = Guid.CreateVersion7();
        var targetPool = string.IsNullOrWhiteSpace(request.TargetPool) ? null : request.TargetPool.Trim();
        catalog.ComputeTasks.Add(new CatalogComputeTask
        {
            TaskId = taskId,
            Operation = request.Operation.Trim(),
            SourceRef = request.SourceRef.Trim(),
            ProviderKind = string.IsNullOrWhiteSpace(request.ProviderKind) ? null : request.ProviderKind.Trim(),
            ArgumentsJson = request.ArgumentsJson,
            TargetPool = targetPool,
            RequestedBy = string.IsNullOrWhiteSpace(request.RequestedBy) ? null : request.RequestedBy.Trim(),
            Status = RunStatuses.Queued,
            EnqueuedUtc = nowUtc,
        });
        await catalog.SaveChangesAsync(ct).ConfigureAwait(false);
        return new ComputeTaskEnqueueResult(taskId, new DispatchTask(taskId, targetPool, nowUtc, false));
    }

    /// <summary>Every queued task's placement row and every running task with its holder, for rebuilding the
    /// dispatcher's memory at activation.</summary>
    public static async Task<(IReadOnlyList<DispatchTask> Queued, IReadOnlyList<RunningTaskRecord> Running)> LoadDispatchStateAsync(
        CatalogDbContext catalog, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var queued = await catalog.ComputeTasks.AsNoTracking()
            .Where(t => t.Status == RunStatuses.Queued)
            .Select(t => new { t.TaskId, t.TargetPool, t.EnqueuedUtc, Cancel = t.CancelRequestedUtc != null })
            .ToListAsync(ct).ConfigureAwait(false);
        var running = await catalog.ComputeTasks.AsNoTracking()
            .Where(t => t.Status == RunStatuses.Running && t.ClaimedByNode != null && t.ClaimedByNode != "")
            .Select(t => new { t.TaskId, t.TargetPool, t.EnqueuedUtc, Cancel = t.CancelRequestedUtc != null, t.ClaimedByNode })
            .ToListAsync(ct).ConfigureAwait(false);
        return (
            queued.Select(t => new DispatchTask(t.TaskId, t.TargetPool, t.EnqueuedUtc, t.Cancel)).ToList(),
            running.Select(t => new RunningTaskRecord(new DispatchTask(t.TaskId, t.TargetPool, t.EnqueuedUtc, t.Cancel), t.ClaimedByNode!)).ToList());
    }

    /// <summary>The ids of every queued task and the holder of every running task, for reconcile.</summary>
    public static async Task<(IReadOnlyList<Guid> QueuedIds, IReadOnlyList<ActiveTaskRef> Running)> ListActiveAsync(
        CatalogDbContext catalog, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var queued = await catalog.ComputeTasks.AsNoTracking()
            .Where(t => t.Status == RunStatuses.Queued)
            .Select(t => t.TaskId)
            .ToListAsync(ct).ConfigureAwait(false);
        var running = await catalog.ComputeTasks.AsNoTracking()
            .Where(t => t.Status == RunStatuses.Running)
            .Select(t => new { t.TaskId, t.ClaimedByNode, Cancel = t.CancelRequestedUtc != null })
            .ToListAsync(ct).ConfigureAwait(false);
        return (queued, running.Select(t => new ActiveTaskRef(t.TaskId, t.ClaimedByNode, t.Cancel)).ToList());
    }

    /// <summary>The placement rows of the given tasks that are still queued.</summary>
    public static async Task<IReadOnlyList<DispatchTask>> LoadQueuedAsync(
        CatalogDbContext catalog, IReadOnlyCollection<Guid> taskIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(taskIds);
        if (taskIds.Count == 0)
        {
            return [];
        }

        var ids = taskIds.ToList();
        var rows = await catalog.ComputeTasks.AsNoTracking()
            .Where(t => t.Status == RunStatuses.Queued && ids.Contains(t.TaskId))
            .Select(t => new { t.TaskId, t.TargetPool, t.EnqueuedUtc, Cancel = t.CancelRequestedUtc != null })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(t => new DispatchTask(t.TaskId, t.TargetPool, t.EnqueuedUtc, t.Cancel)).ToList();
    }

    /// <summary>The placement rows and holders of the given tasks that are still running under a recorded node.</summary>
    public static async Task<IReadOnlyList<RunningTaskRecord>> LoadRunningAsync(
        CatalogDbContext catalog, IReadOnlyCollection<Guid> taskIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(taskIds);
        if (taskIds.Count == 0)
        {
            return [];
        }

        var ids = taskIds.ToList();
        var rows = await catalog.ComputeTasks.AsNoTracking()
            .Where(t => t.Status == RunStatuses.Running && t.ClaimedByNode != null && t.ClaimedByNode != "" && ids.Contains(t.TaskId))
            .Select(t => new { t.TaskId, t.TargetPool, t.EnqueuedUtc, Cancel = t.CancelRequestedUtc != null, t.ClaimedByNode })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows
            .Select(t => new RunningTaskRecord(new DispatchTask(t.TaskId, t.TargetPool, t.EnqueuedUtc, t.Cancel), t.ClaimedByNode!))
            .ToList();
    }

    /// <summary>Journals a hand-out: <c>queued</c> to <c>running</c> under <paramref name="node"/>. Conditional on the
    /// row still being queued, so a task cancelled directly in the meantime is never handed out.</summary>
    public static async Task<bool> MarkHandedOutAsync(
        CatalogDbContext catalog, Guid taskId, string node, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(node);

        var written = await catalog.ComputeTasks
            .Where(t => t.TaskId == taskId && t.Status == RunStatuses.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, RunStatuses.Running)
                .SetProperty(t => t.ClaimedByNode, node)
                .SetProperty(t => t.StartUtc, nowUtc), ct)
            .ConfigureAwait(false);
        return written > 0;
    }

    /// <summary>Records a task's outcome as its node reported it, fenced on the node: the result document on success,
    /// the (secret-redacted) reason on failure, or an honored operator cancel. Returns whether the write applied.</summary>
    public static Task<bool> RecordOutcomeAsync(
        CatalogDbContext catalog, Guid taskId, string node, TaskOutcomeKind outcome, string? failure, string? resultJson,
        DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(node);

        return outcome switch
        {
            TaskOutcomeKind.Succeeded => CompleteAsync(catalog, taskId, resultJson ?? "{}", nowUtc, node, ct),
            TaskOutcomeKind.Failed => FailAsync(
                catalog, taskId, string.IsNullOrWhiteSpace(failure) ? "the task failed without a recorded reason." : failure,
                nowUtc, node, ct),
            _ => CancelRunningAsync(catalog, taskId, nowUtc, node, ct),
        };
    }

    /// <summary>Records a task's success: stores the result JSON and flips it to <c>succeeded</c>. Conditional on the
    /// task still being <c>running</c> (and, with a node fence, still held by that node), so a late completion never
    /// overwrites a cancel or an expiry that already went terminal. Returns true when recorded.</summary>
    public static async Task<bool> CompleteAsync(
        CatalogDbContext catalog, Guid taskId, string resultJson, DateTime nowUtc, string? node = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(resultJson);

        var completed = await catalog.ComputeTasks
            .Where(t => t.TaskId == taskId && t.Status == RunStatuses.Running)
            .Where(t => node == null || t.ClaimedByNode == node)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, RunStatuses.Succeeded)
                .SetProperty(t => t.ResultJson, resultJson)
                .SetProperty(t => t.Error, (string?)null)
                .SetProperty(t => t.EndUtc, nowUtc), ct)
            .ConfigureAwait(false);
        return completed > 0;
    }

    /// <summary>Drives a task to <c>failed</c> with a (secret-redacted) reason. A no-op when already terminal, so a
    /// late failure never overwrites a recorded outcome; with a node fence, also a no-op unless that node holds it.</summary>
    public static async Task<bool> FailAsync(
        CatalogDbContext catalog, Guid taskId, string error, DateTime nowUtc, string? node = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(error);

        var failed = await catalog.ComputeTasks
            .Where(t => t.TaskId == taskId && (t.Status == RunStatuses.Queued || t.Status == RunStatuses.Running))
            .Where(t => node == null || (t.Status == RunStatuses.Running && t.ClaimedByNode == node))
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, RunStatuses.Failed)
                .SetProperty(t => t.Error, error)
                .SetProperty(t => t.EndUtc, nowUtc), ct)
            .ConfigureAwait(false);
        return failed > 0;
    }

    /// <summary>Cancels a task, honoring its lifecycle exactly like a run: a still-queued task cancels outright;
    /// a running task gets a durable cancel request stamped for the dispatcher to relay to its node. Each step
    /// is one atomic conditional update, so a task handed out between the checks is caught by the second rather
    /// than lost; re-requesting a pending cancel is idempotent.</summary>
    public static async Task<CancelOutcome> CancelAsync(
        CatalogDbContext catalog, Guid taskId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var cancelled = await catalog.ComputeTasks
            .Where(t => t.TaskId == taskId && t.Status == RunStatuses.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, RunStatuses.Cancelled)
                .SetProperty(t => t.EndUtc, nowUtc), ct)
            .ConfigureAwait(false);
        if (cancelled > 0)
        {
            return CancelOutcome.Cancelled;
        }

        var requested = await catalog.ComputeTasks
            .Where(t => t.TaskId == taskId && t.Status == RunStatuses.Running && t.CancelRequestedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.CancelRequestedUtc, nowUtc), ct)
            .ConfigureAwait(false);
        if (requested > 0)
        {
            return CancelOutcome.CancelRequested;
        }

        var state = await catalog.ComputeTasks.AsNoTracking()
            .Where(t => t.TaskId == taskId)
            .Select(t => (string?)t.Status)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return state switch
        {
            null => CancelOutcome.NotFound,
            RunStatuses.Running => CancelOutcome.CancelRequested,
            _ => CancelOutcome.NotCancellable,
        };
    }

    /// <summary>Records a running task as <c>cancelled</c> after its owning node aborted the in-flight query.
    /// Conditional on still-running (and on the node fence when given), so a task that finished in the same instant
    /// is never overwritten.</summary>
    public static async Task<bool> CancelRunningAsync(
        CatalogDbContext catalog, Guid taskId, DateTime nowUtc, string? node = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var cancelled = await catalog.ComputeTasks
            .Where(t => t.TaskId == taskId && t.Status == RunStatuses.Running)
            .Where(t => node == null || t.ClaimedByNode == node)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, RunStatuses.Cancelled)
                .SetProperty(t => t.Error, "The task was cancelled by an operator while executing.")
                .SetProperty(t => t.EndUtc, nowUtc), ct)
            .ConfigureAwait(false);
        return cancelled > 0;
    }

    /// <summary>Puts an interrupted task (its node's lease lapsed) back to <c>queued</c> with the holder cleared, so
    /// another node executes it. Fenced on the node that held it.</summary>
    public static async Task<bool> RequeueInterruptedAsync(
        CatalogDbContext catalog, Guid taskId, string node, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(node);

        var requeued = await catalog.ComputeTasks
            .Where(t => t.TaskId == taskId && t.Status == RunStatuses.Running && t.ClaimedByNode == node
                && t.CancelRequestedUtc == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, RunStatuses.Queued)
                .SetProperty(t => t.ClaimedByNode, (string?)null)
                .SetProperty(t => t.StartUtc, (DateTime?)null), ct)
            .ConfigureAwait(false);
        if (requeued > 0)
        {
            return true;
        }

        // A task whose operator cancel was pending when its node went silent is recorded cancelled, never
        // resurrected: the cancel intent is authoritative, exactly as for a run.
        await catalog.ComputeTasks
            .Where(t => t.TaskId == taskId && t.Status == RunStatuses.Running && t.ClaimedByNode == node
                && t.CancelRequestedUtc != null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, RunStatuses.Cancelled)
                .SetProperty(t => t.Error, "The task was cancelled by an operator; its node stopped before recording the cancellation.")
                .SetProperty(t => t.EndUtc, nowUtc), ct)
            .ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// Expires stale tasks so an interactive ask always reaches a terminal state in bounded time: a task queued
    /// since before <paramref name="queuedBefore"/> (no node serves its pool, or no node is running at all) and a
    /// task executing since before <paramref name="runningBefore"/> (its node is presumed lost) are failed with a
    /// precise reason. Returns the ids expired, so the dispatcher drops them from memory.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> ExpireAsync(
        CatalogDbContext catalog, DateTime queuedBefore, DateTime runningBefore, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var staleQueued = await catalog.ComputeTasks.AsNoTracking()
            .Where(t => t.Status == RunStatuses.Queued && t.EnqueuedUtc < queuedBefore)
            .Select(t => t.TaskId)
            .ToListAsync(ct).ConfigureAwait(false);
        var staleRunning = await catalog.ComputeTasks.AsNoTracking()
            .Where(t => t.Status == RunStatuses.Running && t.StartUtc != null && t.StartUtc < runningBefore)
            .Select(t => t.TaskId)
            .ToListAsync(ct).ConfigureAwait(false);
        if (staleQueued.Count == 0 && staleRunning.Count == 0)
        {
            return [];
        }

        var queuedMinutes = (nowUtc - queuedBefore).TotalMinutes;
        var queuedError =
            $"No worker claimed the task within {queuedMinutes:0} minutes. Check that a worker node " +
            "is running and, if the task targets a pool, that a node serves that pool.";
        var expiredQueued = await catalog.ComputeTasks
            .Where(t => t.Status == RunStatuses.Queued && staleQueued.Contains(t.TaskId))
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, RunStatuses.Failed)
                .SetProperty(t => t.Error, queuedError)
                .SetProperty(t => t.EndUtc, nowUtc), ct)
            .ConfigureAwait(false);

        var runningHours = (nowUtc - runningBefore).TotalHours;
        var runningError =
            $"The task ran for over {runningHours:0} hours without completing; its node is presumed " +
            "lost. Re-run the task once a worker that can reach the source is back online.";
        var expiredRunning = await catalog.ComputeTasks
            .Where(t => t.Status == RunStatuses.Running && staleRunning.Contains(t.TaskId))
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, RunStatuses.Failed)
                .SetProperty(t => t.Error, runningError)
                .SetProperty(t => t.EndUtc, nowUtc), ct)
            .ConfigureAwait(false);

        if (expiredQueued == staleQueued.Count && expiredRunning == staleRunning.Count)
        {
            return staleQueued.Concat(staleRunning).ToList();
        }

        // Some candidates moved between the read and the write: report only the rows actually expired now.
        var candidates = staleQueued.Concat(staleRunning).ToList();
        return await catalog.ComputeTasks.AsNoTracking()
            .Where(t => candidates.Contains(t.TaskId) && t.Status == RunStatuses.Failed && t.EndUtc == nowUtc)
            .Select(t => t.TaskId)
            .ToListAsync(ct).ConfigureAwait(false);
    }
}
