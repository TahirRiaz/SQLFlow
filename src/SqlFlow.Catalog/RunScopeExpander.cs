using Microsoft.EntityFrameworkCore;
using SqlFlow.Lineage.Graph;

namespace SqlFlow.Catalog;

/// <summary>The ways a manual run can be scoped. A SCHEDULE is not scoped: it runs its member set, and membership
/// is the only selector (see <see cref="CatalogScheduleMember"/>).</summary>
public enum RunScope
{
    /// <summary>One flow (the default single-flow trigger).</summary>
    Flow,

    /// <summary>A flow and all of its transitive descendants (legacy "Node").</summary>
    Node,
}

/// <summary>The stored spelling of a <see cref="RunScope"/>, so the string form lives in exactly one place. Parse
/// with <see cref="RunScopeExpander.TryParseScope"/>.</summary>
public static class RunScopes
{
    /// <summary>One flow, enqueued as a single run.</summary>
    public const string Flow = "flow";

    /// <summary>A flow and all of its transitive descendants.</summary>
    public const string Node = "node";

    /// <summary>The stored spelling of a scope.</summary>
    public static string From(RunScope scope) => scope switch
    {
        RunScope.Node => Node,
        _ => Flow,
    };
}

/// <summary>One flow selected by a scope expansion, with the wave that orders it within the set and the batch it
/// carries (coalesced to <see cref="CatalogPipeline.DefaultBatch"/> when the flow declares none), so a caller can
/// group or filter the set by batch without a second lookup. <see cref="PipelineId"/> travels with the name so a
/// reader of the set can address the flow itself (its detail page, another lookup) instead of only naming it; the
/// expansions all read it off the pipeline row they already join. It is optional because the queue's own callers
/// construct members from a name alone.</summary>
public sealed record RunScopeMember(string FlowName, string FlowKind, int Wave, string Batch, Guid? PipelineId = null);

/// <summary>The result of expanding a set: what it was anchored on (the flow name for Flow/Node, the schedule name
/// for a schedule's member set) and the ordered member flows.</summary>
public sealed record RunScopeExpansion(RunScope Scope, string Anchor, IReadOnlyList<RunScopeMember> Members);

/// <summary>
/// Turns a run scope (Flow / Node) or a schedule's member set into the concrete, ordered set of flows to enqueue.
/// This is the single place that answers "which flows does this execution touch", shared by the read-only preview
/// endpoint, the trigger endpoint and a schedule's fire and plan, so they can never disagree. It reads exactly the data
/// the lineage graph does: the flow-to-flow dependency edges (<see cref="CatalogFlowDependency"/>) for descendants
/// and for ordering, and the schedule's members (<see cref="CatalogScheduleMember"/>) for a schedule. A set is ordered
/// by the edges among its own members (<see cref="InWaves"/>), not by the repository-wide wave
/// (<see cref="CatalogPipeline.Wave"/>): a flow the set does not run cannot hold back one it does. Only active pipelines are ever selected, so a
/// flow that has left the estate is never enqueued; a <c>mode: manual</c> or <c>mode: disabled</c> pipeline is
/// likewise excluded from group membership by default (a Node's anchor is the one exception: naming it IS the
/// manual trigger; and a Node expansion can opt into "find all" to replay them deliberately). Stateless, like the
/// rest of the catalog stores.
/// </summary>
public static class RunScopeExpander
{
    /// <summary>Parses a scope string from the API (case-insensitive), or null when it is not a known scope.</summary>
    public static RunScope? TryParseScope(string? scope) => scope?.Trim().ToLowerInvariant() switch
    {
        "flow" or "" or null => RunScope.Flow,
        "node" => RunScope.Node,
        _ => null,
    };

    /// <summary>
    /// Expands <paramref name="scope"/> into its ordered member flows, starting from the flow
    /// <paramref name="anchorFlow"/> names (a whole source runs through its schedule instead: see
    /// <see cref="ExpandScheduleAsync"/>). The returned members are active pipelines ordered by wave then name, the
    /// waves taken from the dependencies among the members alone (<see cref="InWaves"/>), so a set lineage has not
    /// analysed yet, which has no edges, runs as a single parallel wave. A Node expansion selects only <c>mode: auto</c> descendants by default
    /// (find only active); <paramref name="includeAll"/> widens it to every descendant, manual and disabled alike
    /// (find all), for the operator who deliberately wants a retired branch replayed with its parent.
    /// </summary>
    public static async Task<RunScopeExpansion> ExpandAsync(
        CatalogDbContext catalog, Guid repoId, string? anchorFlow, RunScope scope, bool includeAll = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        return scope switch
        {
            RunScope.Flow => await ExpandFlowAsync(catalog, repoId, anchorFlow, ct).ConfigureAwait(false),
            RunScope.Node => await ExpandNodeAsync(catalog, repoId, anchorFlow, includeAll, ct).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown run scope."),
        };
    }

    /// <summary>
    /// The member set of a schedule, in wave order: what one fire runs. Membership is the only selector, so this
    /// reads <see cref="CatalogScheduleMember"/> rather than matching any label. <paramref name="batchFilter"/>
    /// optionally narrows the set to members carrying one of those <c>batch:</c> tags, which is how "run the
    /// nightly, but only the small and medium tables" is expressed; it never widens the set, so a filter can only
    /// ever run a subset of what the schedule already owns. A null or empty filter runs every member. Inactive,
    /// <c>mode: manual</c>, and <c>mode: disabled</c> members are excluded exactly as they are from a node
    /// expansion: a manual flow reserved itself for a direct trigger, and a disabled one is deactivated.
    /// </summary>
    public static async Task<RunScopeExpansion> ExpandScheduleAsync(
        CatalogDbContext catalog, Guid repoId, Guid scheduleId, string scheduleName,
        IReadOnlyCollection<string>? batchFilter = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        // A distinct, trimmed set of the requested batches; empty means "no filter" (every member runs). Matched
        // against each flow's batch (coalesced to the default), so EF renders it as a SQL IN over the batch column.
        var filter = batchFilter is null
            ? null
            : batchFilter.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b.Trim())
                .ToHashSet(StringComparer.Ordinal) is { Count: > 0 } set ? set : null;
        var raw = await (
            from member in catalog.ScheduleMembers.AsNoTracking().Where(m => m.ScheduleId == scheduleId)
            join pipeline in catalog.Pipelines.AsNoTracking() on member.PipelineId equals pipeline.Id
            where pipeline.RepoId == repoId && pipeline.Active
                  && pipeline.ExecutionMode == PipelineExecutionModes.Auto
                  && (filter == null || filter.Contains(pipeline.Batch ?? CatalogPipeline.DefaultBatch))
            select new RunScopeMember(
                pipeline.Name, pipeline.Kind, 0, pipeline.Batch ?? CatalogPipeline.DefaultBatch, pipeline.Id))
            .ToListAsync(ct).ConfigureAwait(false);
        if (raw.Count < 2)
        {
            return new RunScopeExpansion(RunScope.Flow, scheduleName, raw);
        }

        var dependencies = await DependenciesAsync(catalog, repoId, ct).ConfigureAwait(false);
        return new RunScopeExpansion(RunScope.Flow, scheduleName, InWaves(raw, dependencies));
    }

    /// <summary>
    /// Orders the flows one execution runs in waves by the dependencies among them alone (<see cref="MemberWaves"/>, the
    /// algorithm a batch flow orders its members by): a member waits for a member it depends on (an edge <c>From</c>
    /// before <c>To</c>, as <see cref="CatalogFlowDependency"/> records them), in as many waves as its longest chain of
    /// such members, and the members of a wave run together. An edge with an end the set does not run orders nothing,
    /// and neither does a path through such a flow: a flow that does not run in this execution changes nothing its
    /// downstream reads, so the repository-wide wave (<see cref="CatalogPipeline.Wave"/>), which counts every flow of the
    /// repository, would only make members wait for work that is not happening. Two cache refreshes that lineage places
    /// after two different deliveries of another schedule thus run side by side in a fire of their own schedule. Members
    /// on a dependency cycle, and those behind one, go to one last wave after every other, as lineage places a cycle in
    /// its fallback wave. Waves are numbered densely from 0; a wave is ordered by flow name. A member without a
    /// <see cref="RunScopeMember.PipelineId"/> has no edges.
    /// </summary>
    /// <param name="members">The flows the execution runs.</param>
    /// <param name="dependencies">The repository's flow-to-flow edges, <c>From</c> finishing before <c>To</c>; edges
    /// with an end outside <paramref name="members"/> are ignored.</param>
    public static IReadOnlyList<RunScopeMember> InWaves(
        IReadOnlyList<RunScopeMember> members, IEnumerable<(Guid From, Guid To)> dependencies)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(dependencies);

        // A member the queue built from a name alone has no pipeline id, so it is keyed by an id no edge names.
        var keys = members.Select(m => m.PipelineId ?? Guid.NewGuid()).ToList();
        var placed = MemberWaves.Compute(keys, dependencies);
        return members
            .Select((member, i) => member with { Wave = placed[i].Wave })
            .OrderBy(m => m.Wave)
            .ThenBy(m => m.FlowName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.FlowName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The repository's flow-to-flow dependency edges, <c>From</c> finishing before <c>To</c>.</summary>
    private static async Task<IReadOnlyList<(Guid From, Guid To)>> DependenciesAsync(
        CatalogDbContext catalog, Guid repoId, CancellationToken ct)
    {
        var edges = await catalog.FlowDependencies.AsNoTracking()
            .Where(d => d.RepoId == repoId)
            .Select(d => new { d.FromPipelineId, d.ToPipelineId })
            .ToListAsync(ct).ConfigureAwait(false);
        return edges.Select(e => (e.FromPipelineId, e.ToPipelineId)).ToList();
    }

    private static async Task<RunScopeExpansion> ExpandFlowAsync(
        CatalogDbContext catalog, Guid repoId, string? anchorFlow, CancellationToken ct)
    {
        var flowName = RequireAnchor(anchorFlow);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var member = await catalog.Pipelines.AsNoTracking()
            .Where(p => p.Id == pipelineId && p.RepoId == repoId && p.Active)
            .Select(p => new RunScopeMember(p.Name, p.Kind, 0, p.Batch ?? CatalogPipeline.DefaultBatch, p.Id))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var members = member is null ? Array.Empty<RunScopeMember>() : new[] { member };
        return new RunScopeExpansion(RunScope.Flow, flowName, members);
    }

    private static async Task<RunScopeExpansion> ExpandNodeAsync(
        CatalogDbContext catalog, Guid repoId, string? anchorFlow, bool includeAll, CancellationToken ct)
    {
        var flowName = RequireAnchor(anchorFlow);
        var anchorId = CatalogIdentity.Pipeline(repoId, flowName);

        // The anchor must itself be an active flow; if it left the estate there is nothing to run.
        var anchorActive = await catalog.Pipelines.AsNoTracking()
            .AnyAsync(p => p.Id == anchorId && p.RepoId == repoId && p.Active, ct).ConfigureAwait(false);
        if (!anchorActive)
        {
            return new RunScopeExpansion(RunScope.Node, flowName, Array.Empty<RunScopeMember>());
        }

        // The flow-to-flow dependency edges for this repo: From must finish before To. Reachability from the anchor
        // over From -> To is exactly its transitive descendant set (the same closure the lineage graph highlights).
        var edges = await DependenciesAsync(catalog, repoId, ct).ConfigureAwait(false);

        var outgoing = new Dictionary<Guid, List<Guid>>();
        foreach (var (from, to) in edges)
        {
            if (!outgoing.TryGetValue(from, out var targets))
            {
                targets = new List<Guid>();
                outgoing[from] = targets;
            }

            targets.Add(to);
        }

        var reachable = new HashSet<Guid> { anchorId };
        var queue = new Queue<Guid>();
        queue.Enqueue(anchorId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!outgoing.TryGetValue(current, out var neighbors))
            {
                continue;
            }

            foreach (var next in neighbors)
            {
                if (reachable.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        // Only mode: auto descendants are selected by default (find only active): a group is automatic
        // execution, and a manual or disabled (deactivated) descendant opted out of exactly that. The anchor
        // itself is always kept, because the caller named it explicitly and a direct request IS the manual
        // trigger; includeAll (find all) widens the set to every descendant for a deliberate full replay.
        var members = await MembersByIdAsync(catalog, repoId, reachable, anchorId, includeAll, ct).ConfigureAwait(false);
        return new RunScopeExpansion(RunScope.Node, flowName, InWaves(members, edges));
    }

    private static async Task<IReadOnlyList<RunScopeMember>> MembersByIdAsync(
        CatalogDbContext catalog, Guid repoId, IReadOnlyCollection<Guid> pipelineIds, Guid anchorId, bool includeAll,
        CancellationToken ct)
    {
        if (pipelineIds.Count == 0)
        {
            return Array.Empty<RunScopeMember>();
        }

        var ids = pipelineIds.ToList();
        return await catalog.Pipelines.AsNoTracking()
            .Where(p => p.RepoId == repoId && p.Active && ids.Contains(p.Id)
                        && (includeAll || p.Id == anchorId || p.ExecutionMode == PipelineExecutionModes.Auto))
            .Select(p => new RunScopeMember(p.Name, p.Kind, 0, p.Batch ?? CatalogPipeline.DefaultBatch, p.Id))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    private static string RequireAnchor(string? anchorFlow)
    {
        var trimmed = anchorFlow?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ArgumentException("A flow-scoped or node-scoped run requires an anchor flow name.", nameof(anchorFlow));
        }

        return trimmed;
    }
}
