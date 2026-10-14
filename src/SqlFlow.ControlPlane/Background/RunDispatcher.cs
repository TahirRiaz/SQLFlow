using System.Text.Json.Nodes;
using SqlFlow.Catalog;
using SqlFlow.Core.Runs;
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

    /// <summary>Enqueues a whole run group (a node-scoped trigger or a schedule's fire) as one wave-ordered set and
    /// returns the group id with its member run ids, so the caller can point a client at the group view.</summary>
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

/// <summary>
/// The one implementation: journals through the stores, then tells the local <see cref="Dispatcher"/>. Every run, every
/// member of a group and every compute task is queued carrying the values the control plane supplies for its references
/// (<see cref="RunReferences"/>), unless its caller already composed them: this is the one place every way a run reaches
/// the queue passes through (an endpoint, a schedule fire, a group, the CLI through the API), so a scheduled run and a
/// run someone pressed a button for resolve the same values.
/// </summary>
public sealed class InProcessRunDispatcher : IRunDispatcher
{
    private readonly Dispatcher _dispatcher;
    private readonly TimeProvider _clock;
    private readonly RunReferences _references;

    public InProcessRunDispatcher(Dispatcher dispatcher, TimeProvider clock, RunReferences references)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(references);
        _dispatcher = dispatcher;
        _clock = clock;
        _references = references;
    }

    public async Task<Guid> EnqueueAsync(CatalogDbContext catalog, RunEnqueueRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var parameters = request.Parameters ?? RunParameters.None;
        if (parameters.References.Count == 0 && _references.HasSources)
        {
            var supplied = await _references.ForAsync(request.RepoId, ct).ConfigureAwait(false);
            if (supplied.Count > 0)
            {
                request = request with { Parameters = parameters with { References = supplied } };
            }
        }

        var result = await RunQueueStore
            .EnqueueAsync(catalog, request, _clock.GetUtcNow().UtcDateTime, ct)
            .ConfigureAwait(false);
        _dispatcher.NotifyRunsEnqueued([result.Placement]);
        return result.RunId;
    }

    public async Task<RunGroupEnqueueResult> EnqueueGroupAsync(
        CatalogDbContext catalog, RunGroupEnqueueRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_references.HasSources)
        {
            var supplied = await _references.ForAsync(request.RepoId, ct).ConfigureAwait(false);
            if (supplied.Count > 0)
            {
                // Every member, one the caller gave parameters of its own or not, carries the repository's values; a member
                // whose caller composed references keeps them.
                var members = new Dictionary<string, RunParameters>(
                    request.MemberParameters ?? new Dictionary<string, RunParameters>(StringComparer.Ordinal), StringComparer.Ordinal);
                foreach (var member in request.Members)
                {
                    var own = members.GetValueOrDefault(member.FlowName) ?? RunParameters.None;
                    if (own.References.Count == 0)
                    {
                        members[member.FlowName] = own with { References = supplied };
                    }
                }

                request = request with { MemberParameters = members };
            }
        }

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
        ArgumentNullException.ThrowIfNull(request);
        if (_references.HasSources)
        {
            request = request with { ArgumentsJson = await WithReferencesAsync(request.ArgumentsJson, ct).ConfigureAwait(false) };
        }

        var result = await ComputeTaskStore
            .EnqueueAsync(catalog, request, _clock.GetUtcNow().UtcDateTime, ct)
            .ConfigureAwait(false);
        _dispatcher.NotifyTaskEnqueued(result.Placement);
        return result.TaskId;
    }

    /// <summary>
    /// A task's payload carrying the values supplied for work of no repository, unless it names references already. The
    /// payload's own shape is the node's to validate; only its <c>references</c> member is read and written here.
    /// </summary>
    private async Task<string> WithReferencesAsync(string argumentsJson, CancellationToken ct)
    {
        JsonObject? payload;
        try
        {
            payload = string.IsNullOrWhiteSpace(argumentsJson) ? null : JsonNode.Parse(argumentsJson) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            payload = null;
        }

        if (payload is null)
        {
            // Not a JSON object: left for the store and the node to refuse with their own messages.
            return argumentsJson;
        }

        if (payload.TryGetPropertyValue(ReferencesMember, out var present) && present is JsonObject { Count: > 0 })
        {
            return argumentsJson;
        }

        var supplied = await _references.ForAsync(null, ct).ConfigureAwait(false);
        if (supplied.Count == 0)
        {
            return argumentsJson;
        }

        var references = new JsonObject();
        foreach (var (name, value) in supplied.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            references[name] = value;
        }

        payload[ReferencesMember] = references;
        return payload.ToJsonString();
    }

    /// <summary>The member of a compute task's payload that holds its supplied references (<see cref="Core.Compute.ComputeTaskPayload.References"/>).</summary>
    private const string ReferencesMember = "references";

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
