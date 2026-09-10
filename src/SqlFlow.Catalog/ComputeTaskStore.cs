using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace SqlFlow.Catalog;

/// <summary>What to enqueue: the operation, the connection REFERENCE (never a secret), the validated payload
/// as JSON, optional pool routing, and the requester for audit. Bundled so the optional fields can never be
/// passed in the wrong order.</summary>
public sealed record ComputeTaskEnqueueRequest(
    string Operation, string SourceRef, string? ProviderKind, string ArgumentsJson,
    string? TargetPool = null, string? RequestedBy = null);

/// <summary>
/// The durable ad-hoc compute queue: the <see cref="CatalogComputeTask"/> table IS the queue, so a requested
/// inspection survives a host restart, is visible to the read API from the moment it is queued, and (with the
/// same atomic claim the run queue uses) can be drained by many workers without ever double-executing. The
/// control plane enqueues; a worker claims the oldest eligible task, executes it through the shared engine's
/// <c>ComputeTaskExecutor</c>, and records the result or error on the same row. Stateless, like
/// <see cref="RunQueueStore"/>.
/// </summary>
public static class ComputeTaskStore
{
    /// <summary>How long a task may sit unclaimed before it is failed with a routing hint: an interactive ask
    /// with no worker serving its pool (or no worker at all) must answer the operator in bounded time instead
    /// of hanging "queued" forever. Applied lazily by <see cref="ExpireAsync"/> on the read path.</summary>
    public static readonly TimeSpan QueuedExpiry = TimeSpan.FromMinutes(15);

    /// <summary>How long a claimed task may run before it is presumed lost (its node died and never came back
    /// to recover its orphans) and failed. Generous, because unique-key detection on a huge table is legitimate
    /// long work; a node RESTART recovers its own orphans far sooner via <see cref="RecoverStuckRunningAsync"/>.</summary>
    public static readonly TimeSpan RunningExpiry = TimeSpan.FromHours(6);

    // The same reliable single-statement claim the run queue uses: atomically pick the oldest queued task and
    // flip it to running, returning its id. UPDLOCK avoids the lock-upgrade race, READPAST lets concurrent
    // workers each take a different task, ROWLOCK keeps the lock granular, and the leading SET pins READ
    // COMMITTED because READPAST is illegal under a leftover SERIALIZABLE session level on a pooled connection.
    // {POOL_PREDICATE} is replaced with a parameterized pool filter (names bound as parameters, never
    // interpolated).
    private const string ClaimSqlTemplate = """
        SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
        UPDATE [catalog].[ComputeTask]
        SET [Status] = @running, [ClaimedByNode] = @node, [StartUtc] = @now
        OUTPUT inserted.[TaskId]
        WHERE [TaskId] = (
            SELECT TOP (1) t.[TaskId] FROM [catalog].[ComputeTask] AS t WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE t.[Status] = @queued AND {POOL_PREDICATE}
            ORDER BY t.[EnqueuedUtc], t.[TaskId]);
        """;

    /// <summary>Enqueues a task: inserts a <c>queued</c> row and returns its newly minted (time-ordered) id, so
    /// the caller can point the client at <c>GET /datasources/tasks/{id}</c> immediately.</summary>
    public static async Task<Guid> EnqueueAsync(
        CatalogDbContext catalog, ComputeTaskEnqueueRequest request, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ArgumentsJson);

        var taskId = Guid.CreateVersion7();
        catalog.ComputeTasks.Add(new CatalogComputeTask
        {
            TaskId = taskId,
            Operation = request.Operation.Trim(),
            SourceRef = request.SourceRef.Trim(),
            ProviderKind = string.IsNullOrWhiteSpace(request.ProviderKind) ? null : request.ProviderKind.Trim(),
            ArgumentsJson = request.ArgumentsJson,
            TargetPool = string.IsNullOrWhiteSpace(request.TargetPool) ? null : request.TargetPool.Trim(),
            RequestedBy = string.IsNullOrWhiteSpace(request.RequestedBy) ? null : request.RequestedBy.Trim(),
            Status = RunStatuses.Queued,
            EnqueuedUtc = nowUtc,
        });
        await catalog.SaveChangesAsync(ct).ConfigureAwait(false);
        return taskId;
    }

    /// <summary>Atomically claims the oldest queued task this node is eligible for (untargeted, or routed to one
    /// of <paramref name="pools"/>), flipping it to <c>running</c> and returning its id, or null when there is
    /// none. Safe to call concurrently from many workers.</summary>
    public static async Task<Guid?> ClaimNextAsync(
        CatalogDbContext catalog, string node, IReadOnlyList<string> pools, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        ArgumentNullException.ThrowIfNull(pools);

        var poolPredicate = "t.[TargetPool] IS NULL";
        if (pools.Count > 0)
        {
            var placeholders = string.Join(", ", pools.Select((_, i) => $"@pool{i}"));
            poolPredicate = $"(t.[TargetPool] IS NULL OR t.[TargetPool] IN ({placeholders}))";
        }

        var sql = ClaimSqlTemplate.Replace("{POOL_PREDICATE}", poolPredicate, StringComparison.Ordinal);

        var strategy = catalog.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            var connection = catalog.Database.GetDbConnection();
            await catalog.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                if (catalog.Database.CurrentTransaction is { } tx)
                {
                    command.Transaction = tx.GetDbTransaction();
                }

                AddParameter(command, "@running", RunStatuses.Running);
                AddParameter(command, "@queued", RunStatuses.Queued);
                AddParameter(command, "@node", node);
                AddParameter(command, "@now", nowUtc);
                for (var i = 0; i < pools.Count; i++)
                {
                    AddParameter(command, $"@pool{i}", pools[i]);
                }

                var scalar = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
                return scalar is Guid claimed ? claimed : (Guid?)null;
            }
            finally
            {
                await catalog.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    /// <summary>Records a claimed task's success: stores the result JSON and flips it to <c>succeeded</c>.
    /// Conditional on the task still being <c>running</c>, so a late completion never overwrites a cancel or an
    /// expiry that already went terminal. Returns true when recorded.</summary>
    public static async Task<bool> CompleteAsync(
        CatalogDbContext catalog, Guid taskId, string resultJson, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(resultJson);

        var completed = await catalog.ComputeTasks
            .Where(t => t.TaskId == taskId && t.Status == RunStatuses.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, RunStatuses.Succeeded)
                .SetProperty(t => t.ResultJson, resultJson)
                .SetProperty(t => t.Error, (string?)null)
                .SetProperty(t => t.EndUtc, nowUtc), ct)
            .ConfigureAwait(false);
        return completed > 0;
    }

    /// <summary>Drives a task to <c>failed</c> with a (secret-redacted) reason. A no-op when already terminal,
    /// so a late failure never overwrites a recorded outcome.</summary>
    public static Task<int> FailAsync(
        CatalogDbContext catalog, Guid taskId, string error, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(error);

        return catalog.ComputeTasks
            .Where(t => t.TaskId == taskId && (t.Status == RunStatuses.Queued || t.Status == RunStatuses.Running))
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, RunStatuses.Failed)
                .SetProperty(t => t.Error, error)
                .SetProperty(t => t.EndUtc, nowUtc), ct);
    }

    /// <summary>Cancels a task, honoring its lifecycle exactly like a run: a still-queued task cancels outright;
    /// a running task gets a durable cancel request stamped for its owning node to observe and abort. Each step
    /// is one atomic conditional update, so a task claimed between the checks is caught by the second rather
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

    /// <summary>The ids of tasks this node is executing that an operator asked to cancel; the worker polls this
    /// to trip the matching task's cancellation token. Scoped to the node, like the run equivalent.</summary>
    public static Task<List<Guid>> ListCancelRequestedAsync(
        CatalogDbContext catalog, string node, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(node);

        return catalog.ComputeTasks.AsNoTracking()
            .Where(t => t.Status == RunStatuses.Running && t.ClaimedByNode == node && t.CancelRequestedUtc != null)
            .Select(t => t.TaskId)
            .ToListAsync(ct);
    }

    /// <summary>Records a running task as <c>cancelled</c> after its owning node aborted the in-flight query.
    /// Conditional on still-running, so a task that finished in the same instant is never overwritten.</summary>
    public static Task<int> CancelRunningAsync(
        CatalogDbContext catalog, Guid taskId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        return catalog.ComputeTasks
            .Where(t => t.TaskId == taskId && t.Status == RunStatuses.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, RunStatuses.Cancelled)
                .SetProperty(t => t.Error, "The task was cancelled by an operator while executing.")
                .SetProperty(t => t.EndUtc, nowUtc), ct);
    }

    /// <summary>Requeues tasks left <c>running</c> by this node: on worker startup they are orphans from a
    /// previous incarnation that stopped mid-task. Returns the number recovered.</summary>
    public static Task<int> RecoverStuckRunningAsync(CatalogDbContext catalog, string node, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(node);

        return catalog.ComputeTasks
            .Where(t => t.Status == RunStatuses.Running && t.ClaimedByNode == node)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, RunStatuses.Queued)
                .SetProperty(t => t.ClaimedByNode, (string?)null)
                .SetProperty(t => t.StartUtc, (DateTime?)null), ct);
    }

    /// <summary>
    /// Lazily expires stale tasks so an interactive ask always reaches a terminal state in bounded time: a task
    /// queued longer than <see cref="QueuedExpiry"/> (no worker serves its pool, or no worker is running at all)
    /// and a task running longer than <see cref="RunningExpiry"/> (its node died and never restarted) are failed
    /// with a precise reason. Called from the control plane's task read path, so expiry needs no extra
    /// background service and costs nothing while the queue is healthy. Returns how many tasks were expired.
    /// </summary>
    public static async Task<int> ExpireAsync(CatalogDbContext catalog, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var queuedBefore = nowUtc - QueuedExpiry;
        var queuedError =
            $"No worker claimed the task within {QueuedExpiry.TotalMinutes:0} minutes. Check that a worker node " +
            "is running and, if the task targets a pool, that a node serves that pool.";
        var expiredQueued = await catalog.ComputeTasks
            .Where(t => t.Status == RunStatuses.Queued && t.EnqueuedUtc < queuedBefore)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, RunStatuses.Failed)
                .SetProperty(t => t.Error, queuedError)
                .SetProperty(t => t.EndUtc, nowUtc), ct)
            .ConfigureAwait(false);

        var runningBefore = nowUtc - RunningExpiry;
        var runningError =
            $"The task ran for over {RunningExpiry.TotalHours:0} hours without completing; its node is presumed " +
            "lost. Re-run the task once a worker that can reach the source is back online.";
        var expiredRunning = await catalog.ComputeTasks
            .Where(t => t.Status == RunStatuses.Running && t.StartUtc != null && t.StartUtc < runningBefore)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, RunStatuses.Failed)
                .SetProperty(t => t.Error, runningError)
                .SetProperty(t => t.EndUtc, nowUtc), ct)
            .ConfigureAwait(false);

        return expiredQueued + expiredRunning;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
