using SqlFlow.Catalog;
using SqlFlow.Dispatch;
using SqlFlow.Dispatch.Protocol;
using SqlFlow.Yaml;

namespace SqlFlow.ControlPlane.Dispatch;

/// <summary>
/// The dispatcher's journal over the shadow catalog: every port operation opens its own catalog scope (the pooled,
/// no-tracking context the API uses) and delegates to the stateless stores, so the dispatcher never holds a
/// connection and calls from hundreds of concurrent polls never share one. Nothing here is more than a plain
/// conditional update, insert or select; every fence lives in the store it delegates to. The execution-support
/// operations (the hand-out spec, the flow version, the lineage context, the live trace) are the same stores the
/// node used to call over its own catalog connection, now reached only through the dispatcher.
/// </summary>
public sealed class CatalogDispatchLedger : IDispatchLedger
{
    private readonly IServiceScopeFactory _scopes;

    public CatalogDispatchLedger(IServiceScopeFactory scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        _scopes = scopes;
    }

    public Task<DispatchLedgerSnapshot> LoadAsync(CancellationToken ct)
        => WithCatalogAsync(async catalog =>
        {
            var (queuedRuns, runningRuns) = await RunQueueStore.LoadDispatchStateAsync(catalog, ct).ConfigureAwait(false);
            var (queuedTasks, runningTasks) = await ComputeTaskStore.LoadDispatchStateAsync(catalog, ct).ConfigureAwait(false);
            return new DispatchLedgerSnapshot(queuedRuns, runningRuns, queuedTasks, runningTasks);
        });

    public Task<DispatchActiveIds> ListActiveAsync(CancellationToken ct)
        => WithCatalogAsync(async catalog =>
        {
            var (queuedRuns, runningRuns) = await RunQueueStore.ListActiveAsync(catalog, ct).ConfigureAwait(false);
            var (queuedTasks, runningTasks) = await ComputeTaskStore.ListActiveAsync(catalog, ct).ConfigureAwait(false);
            return new DispatchActiveIds(queuedRuns, runningRuns, queuedTasks, runningTasks);
        });

    public Task<IReadOnlyList<DispatchRun>> LoadQueuedRunsAsync(IReadOnlyCollection<Guid> runIds, CancellationToken ct)
        => WithCatalogAsync(catalog => RunQueueStore.LoadQueuedAsync(catalog, runIds, ct));

    public Task<IReadOnlyList<DispatchTask>> LoadQueuedTasksAsync(IReadOnlyCollection<Guid> taskIds, CancellationToken ct)
        => WithCatalogAsync(catalog => ComputeTaskStore.LoadQueuedAsync(catalog, taskIds, ct));

    public Task<IReadOnlyList<RunningRunRecord>> LoadRunningRunsAsync(IReadOnlyCollection<Guid> runIds, CancellationToken ct)
        => WithCatalogAsync(catalog => RunQueueStore.LoadRunningAsync(catalog, runIds, ct));

    public Task<IReadOnlyList<RunningTaskRecord>> LoadRunningTasksAsync(IReadOnlyCollection<Guid> taskIds, CancellationToken ct)
        => WithCatalogAsync(catalog => ComputeTaskStore.LoadRunningAsync(catalog, taskIds, ct));

    public Task<RunSpec?> MarkRunHandedOutAsync(Guid runId, int expectedAttempt, string node, DateTime nowUtc, CancellationToken ct)
        => WithCatalogAsync(catalog => RunQueueStore.MarkHandedOutAsync(catalog, runId, expectedAttempt, node, nowUtc, ct));

    public Task<RunOutcomeRecord> RecordRunOutcomeAsync(
        Guid runId, string node, int attempt, RunOutcomeKind outcome, string? failure, string? artifactJson,
        DateTime nowUtc, CancellationToken ct)
        => WithScopeAsync((services, catalog) =>
        {
            // A registered kind's result object is recorded on the run row, where a fan-out root and the run page read
            // it; a built-in kind's result is projected into the typed columns instead.
            var documents = services.GetRequiredService<YamlDocumentLoader>();
            return RunQueueStore.RecordOutcomeAsync(
                catalog, runId, node, attempt, outcome, failure, artifactJson, nowUtc,
                projectsResult: kind => documents.FindKind(kind) is not null, ct: ct);
        });

    public Task<FanOutEnqueueRecord> EnqueueFanOutAsync(Guid rootRunId, FanOutRequest request, DateTime nowUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return WithScopeAsync((services, catalog) =>
        {
            // Each member's parameters pass the root kind's own rule, exactly as a triggered run's do.
            var documents = services.GetRequiredService<YamlDocumentLoader>();
            return RunQueueStore.EnqueueFanOutAsync(
                catalog, rootRunId, request.Node, request.Attempt, request.Members, nowUtc, documents.ValidateRunParameters, ct);
        });
    }

    public Task<FanOutStateResponse> LoadFanOutStateAsync(Guid rootRunId, Guid groupId, FanOutFence fence, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fence);
        return WithCatalogAsync(catalog => RunQueueStore.LoadFanOutStateAsync(
            catalog, rootRunId, groupId, fence.Node, fence.Attempt, ct));
    }

    public Task<FanOutCancelResponse> CancelFanOutAsync(Guid rootRunId, Guid groupId, FanOutFence fence, DateTime nowUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fence);
        return WithCatalogAsync(catalog => RunQueueStore.CancelFanOutAsync(
            catalog, rootRunId, groupId, fence.Node, fence.Attempt, nowUtc, ct));
    }

    public Task<InterruptedRunRecord> RequeueInterruptedRunAsync(Guid runId, string node, int attempt, DateTime nowUtc, CancellationToken ct)
        => WithCatalogAsync(catalog => RunQueueStore.RequeueInterruptedAsync(catalog, runId, node, attempt, nowUtc, ct));

    public Task<InterruptedRunRecord> FailInterruptedRunAsync(Guid runId, string node, int attempt, DateTime nowUtc, CancellationToken ct)
        => WithCatalogAsync(catalog => RunQueueStore.FailInterruptedAsync(catalog, runId, node, attempt, nowUtc, ct));

    public Task<InterruptedRunRecord> CancelInterruptedRunAsync(Guid runId, string node, int attempt, DateTime nowUtc, CancellationToken ct)
        => WithCatalogAsync(catalog => RunQueueStore.CancelInterruptedAsync(catalog, runId, node, attempt, nowUtc, ct));

    public Task<TaskSpec?> MarkTaskHandedOutAsync(Guid taskId, string node, DateTime nowUtc, CancellationToken ct)
        => WithCatalogAsync(catalog => ComputeTaskStore.MarkHandedOutAsync(catalog, taskId, node, nowUtc, ct));

    public Task<bool> RecordTaskOutcomeAsync(
        Guid taskId, string node, TaskOutcomeKind outcome, string? failure, string? resultJson, DateTime nowUtc, CancellationToken ct)
        => WithCatalogAsync(catalog => ComputeTaskStore.RecordOutcomeAsync(catalog, taskId, node, outcome, failure, resultJson, nowUtc, ct));

    public Task<bool> RequeueInterruptedTaskAsync(Guid taskId, string node, DateTime nowUtc, CancellationToken ct)
        => WithCatalogAsync(catalog => ComputeTaskStore.RequeueInterruptedAsync(catalog, taskId, node, nowUtc, ct));

    public Task<IReadOnlyList<Guid>> ExpireTasksAsync(DateTime queuedBefore, DateTime runningBefore, DateTime nowUtc, CancellationToken ct)
        => WithCatalogAsync(catalog => ComputeTaskStore.ExpireAsync(catalog, queuedBefore, runningBefore, nowUtc, ct));

    public Task<string?> LoadFlowVersionAsync(string contentHash, CancellationToken ct)
        => WithCatalogAsync(catalog => RunQueueStore.LoadFlowVersionAsync(catalog, contentHash, ct));

    public Task<RunContextResponse> ResolveRunContextAsync(Guid runId, RunContextRequest request, CancellationToken ct)
        => WithCatalogAsync(catalog => RunContextStore.ResolveAsync(catalog, runId, request, ct));

    public Task<bool> AppendRunTraceAsync(Guid runId, RunTraceBatch batch, CancellationToken ct)
        => WithCatalogAsync(catalog => RunTraceStore.AppendLiveAsync(catalog, runId, batch, ct));

    public Task<DateTime?> RecordNodeHeartbeatAsync(NodeHeartbeat heartbeat, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);
        return WithCatalogAsync(catalog => NodeStore.HeartbeatAsync(
            catalog, heartbeat.Name, heartbeat.Version, heartbeat.LastSeenUtc, heartbeat.Pool, heartbeat.BusyRuns,
            heartbeat.RunSlots, ct));
    }

    public Task<int> PruneNodesAsync(DateTime olderThanUtc, CancellationToken ct)
        => WithCatalogAsync(catalog => NodeStore.PruneStaleAsync(catalog, olderThanUtc, ct));

    public Task<bool> TryAcquireOwnershipAsync(string owner, DateTime nowUtc, TimeSpan ttl, CancellationToken ct)
        => WithCatalogAsync(catalog => DispatchLeaseStore.TryAcquireAsync(
            catalog, DispatchLeaseStore.DispatchLeaseName, owner, nowUtc, ttl, ct));

    public Task ReleaseOwnershipAsync(string owner, CancellationToken ct)
        => WithCatalogAsync(async catalog =>
        {
            await DispatchLeaseStore.ReleaseAsync(catalog, DispatchLeaseStore.DispatchLeaseName, owner, DateTime.UtcNow, ct)
                .ConfigureAwait(false);
            return true;
        });

    private Task<T> WithCatalogAsync<T>(Func<CatalogDbContext, Task<T>> operation)
        => WithScopeAsync((_, catalog) => operation(catalog));

    private async Task<T> WithScopeAsync<T>(Func<IServiceProvider, CatalogDbContext, Task<T>> operation)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        return await operation(scope.ServiceProvider, catalog).ConfigureAwait(false);
    }
}
