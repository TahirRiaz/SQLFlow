using Microsoft.EntityFrameworkCore;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Runs;
using SqlFlow.Dispatch.Protocol;

namespace SqlFlow.Catalog;

/// <summary>
/// The lineage facts a run's execution depends on, resolved from the catalog's lineage graph and run ledger on
/// behalf of the node executing it (the engine tier has no catalog, and since the node protocol carries the run's
/// whole context, neither has the node): the downstream table an incremental flow anchors its watermark to, and
/// whether a chained landing table may be reset before this run's load. Both are relative to the flow's own
/// target, which the node names from the parsed document; the run's parameters, which a backfill window feeds
/// into the reset verdict, are read from the run row. Stateless, like every store here.
/// </summary>
public static class RunContextStore
{
    /// <summary>Resolves what the request asks for, fenced on the run still executing under the requester's node
    /// and attempt (the journal-side half of the fence the dispatcher checks in memory first).</summary>
    public static async Task<RunContextResponse> ResolveAsync(
        CatalogDbContext catalog, Guid runId, RunContextRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Node);

        var run = await catalog.Runs.AsNoTracking()
            .Where(r => r.RunId == runId && r.Status == RunStatuses.Running
                && r.ClaimedByNode == request.Node && r.Attempt == request.Attempt)
            .Select(r => new
            {
                r.RepoId,
                r.PipelineId,
                r.FullLoad,
                r.BackfillFrom,
                r.BackfillTo,
                r.FilePattern,
                r.SourceFilter,
                r.AssertionsOnly,
                r.ReprocessFromSourceMin,
            })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (run is null)
        {
            return RunContextResponse.NotHeld;
        }

        // A run with no repo attribution has no lineage to consult: both facts fall back to their defaults (the
        // probe stays on the flow's own target, the landing table is left alone).
        if (run.RepoId is not { } repoId)
        {
            return new RunContextResponse(true, null, null);
        }

        RelationalObject? watermark = null;
        if (request.ResolveWatermark)
        {
            watermark = await ResolveDownstreamWatermarkTableAsync(
                catalog, repoId, run.PipelineId, request.TargetSchema, request.TargetTable, ct).ConfigureAwait(false);
        }

        LandingReset? landingReset = null;
        if (request.ResolveLandingReset)
        {
            var parameters = new RunParameters
            {
                FullLoad = run.FullLoad,
                BackfillFrom = run.BackfillFrom,
                BackfillTo = run.BackfillTo,
                FilePattern = run.FilePattern,
                SourceFilter = run.SourceFilter,
                AssertionsOnly = run.AssertionsOnly,
                ReprocessFromSourceMin = run.ReprocessFromSourceMin,
            };
            landingReset = await ResolveLandingResetAsync(
                catalog, repoId, run.PipelineId, request.TargetSchema, request.TargetTable, parameters, ct).ConfigureAwait(false);
        }

        return new RunContextResponse(true, watermark, landingReset);
    }

    /// <summary>
    /// Resolves the next durable table downstream of this flow in the lineage chain, for downstream-anchored
    /// watermarking (incremental.watermarkFromDownstream). It walks the persisted lineage the sync already
    /// computed: the flow-level dependencies name the flows that consume this one (they read an object it
    /// writes/creates), and each of those flows' Writes/Creates edges name the durable tables they populate. The
    /// flow's own target is excluded (a downstream flow writing back to it is not a "next" table). Anchoring is
    /// applied only when exactly one such table resolves with a full three-part identity: an ambiguous chain (a
    /// fan-out to several tables) or a partially-identified object is left to fall back to the flow's own target,
    /// so the watermark is never anchored to a guessed table. Returns null when there is no unambiguous next table.
    /// </summary>
    public static async Task<RelationalObject?> ResolveDownstreamWatermarkTableAsync(
        CatalogDbContext catalog, Guid repoId, Guid pipelineId, string ownSchema, string ownName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(ownSchema);
        ArgumentNullException.ThrowIfNull(ownName);

        var downstreamPipelineIds = await catalog.FlowDependencies.AsNoTracking()
            .Where(d => d.RepoId == repoId && d.FromPipelineId == pipelineId)
            .Select(d => d.ToPipelineId)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        if (downstreamPipelineIds.Count == 0)
        {
            return null;
        }

        var writeKeys = await catalog.LineageEdges.AsNoTracking()
            .Where(e => e.RepoId == repoId
                && e.PipelineId != null && downstreamPipelineIds.Contains(e.PipelineId.Value)
                && (e.Relation == "Writes" || e.Relation == "Creates"))
            .Select(e => e.ObjectKey)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        if (writeKeys.Count == 0)
        {
            return null;
        }

        // Resolve the write targets to fully-identified table objects (a table, with a database and schema, so the
        // engine can introspect and probe it). Views and partially-resolved objects are dropped here.
        var candidates = await catalog.Objects.AsNoTracking()
            .Where(o => writeKeys.Contains(o.Key)
                && o.Kind == "Table"
                && o.Database != null && o.Schema != null)
            .Select(o => new { o.Database, o.Schema, o.Name })
            .ToListAsync(ct).ConfigureAwait(false);

        // Exclude the flow's own target (a downstream flow writing back to it is not a "next" table). Matched on
        // schema + name only: a file flow's target carries no database part, and a table's schema-qualified name
        // is unique enough within a repo's estate to identify "this is my own target".
        var distinct = candidates
            .Where(c => !(string.Equals(c.Schema, ownSchema, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.Name, ownName, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(c => (
                c.Database!.ToLowerInvariant(),
                c.Schema!.ToLowerInvariant(),
                c.Name.ToLowerInvariant()))
            .Select(g => g.First())
            .ToList();
        if (distinct.Count != 1)
        {
            return null;
        }

        var only = distinct[0];
        return new RelationalObject { Database = only.Database!, Schema = only.Schema!, Name = only.Name };
    }

    /// <summary>
    /// Resolves the landing-reset verdict for a chained landing (file) flow: may the engine truncate the flow's
    /// landing target before this run's load? The verdict is one hop only, by design: the landing (bronze) table
    /// is freed when the NEXT phase (silver) has its data, so the gate is that every flow DIRECTLY consuming this
    /// flow (per the persisted lineage dependencies) has completed a successful run that started after this
    /// flow's last successful run ended, proving everything the last load staged was visible to and consumed by
    /// every reader. What any later phase (gold) holds never enters the verdict. Two additional conditions keep
    /// the truncate honest: every consumer must read the engine's typed view <c>[schema].[v_&lt;table&gt;]</c>
    /// (the chained landing contract; a consumer reading the base table, or through a hand-written object, may
    /// depend on rows accumulating, so the reset is refused rather than guessed), and this flow must have a
    /// prior successful run to anchor the comparison (a seeded table with no run history is never truncated on
    /// faith). A run bounded to a window or filter (an operator backfill of a slice) is refused outright: the
    /// whole staged dataset must reach the downstream merge, so a partial re-land never truncates first; a plain
    /// forced full load keeps the normal gate and becomes a clean staging rebuild when authorized. Returns null
    /// when the flow has no lineage consumers at all: a plain append flow that is nobody's staging area is left
    /// alone without comment. Every blocked verdict carries the reason, so a landing table that keeps growing
    /// has a loud, queryable explanation on each run.
    /// </summary>
    public static async Task<LandingReset?> ResolveLandingResetAsync(
        CatalogDbContext catalog, Guid repoId, Guid pipelineId, string targetSchema, string targetTable,
        RunParameters parameters, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(targetSchema);
        ArgumentNullException.ThrowIfNull(targetTable);
        ArgumentNullException.ThrowIfNull(parameters);

        var consumers = await catalog.FlowDependencies.AsNoTracking()
            .Where(d => d.RepoId == repoId && d.FromPipelineId == pipelineId)
            .Select(d => new { d.ToPipelineId, d.ToFlow })
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        if (consumers.Count == 0)
        {
            return null;
        }

        // A window- or filter-bounded run (an operator backfilling a specific slice) deliberately re-lands only
        // part of the source, and the whole staged dataset must reach the downstream merge: resetting first
        // would leave the landing table holding just the slice, which a downstream full-refresh consumer could
        // then rebuild from. Never reset a bounded run. A plain forced full load (--full, no window or filter)
        // keeps the normal gate below: it re-lands everything the definition selects, so an authorized reset
        // turns it into a clean staging rebuild instead of doubling the table.
        if (parameters.BackfillFrom is not null || parameters.BackfillTo is not null
            || !string.IsNullOrWhiteSpace(parameters.FilePattern) || !string.IsNullOrWhiteSpace(parameters.SourceFilter))
        {
            return new LandingReset
            {
                Authorized = false,
                Reason = "this run is bounded to a window/filter (backfill); a partial re-land never resets the landing table",
            };
        }

        // The chained landing contract: consumers read the engine-generated typed view over the landing table.
        // Resolve which consumers actually read [targetSchema].[v_<targetTable>] from their persisted Reads
        // edges; any consumer that depends on this flow through some other object gets the rows preserved.
        var viewName = $"v_{targetTable}";
        var consumerIds = consumers.Select(c => c.ToPipelineId).ToList();
        var reads = await catalog.LineageEdges.AsNoTracking()
            .Where(e => e.RepoId == repoId
                && e.PipelineId != null && consumerIds.Contains(e.PipelineId.Value)
                && e.Relation == "Reads")
            .Select(e => new { PipelineId = e.PipelineId!.Value, e.ObjectKey })
            .ToListAsync(ct).ConfigureAwait(false);
        var readKeys = reads.Select(r => r.ObjectKey).Distinct().ToList();
        var viewKeys = await catalog.Objects.AsNoTracking()
            .Where(o => readKeys.Contains(o.Key)
                && o.Kind == "View"
                && o.Schema == targetSchema && o.Name == viewName)
            .Select(o => o.Key)
            .ToListAsync(ct).ConfigureAwait(false);
        var viewKeySet = viewKeys.ToHashSet(StringComparer.Ordinal);
        var viewReaders = reads.Where(r => viewKeySet.Contains(r.ObjectKey)).Select(r => r.PipelineId).ToHashSet();
        var nonViewConsumer = consumers.FirstOrDefault(c => !viewReaders.Contains(c.ToPipelineId));
        if (nonViewConsumer is not null)
        {
            return new LandingReset
            {
                Authorized = false,
                Reason = $"consumer '{nonViewConsumer.ToFlow}' does not read the typed view [{targetSchema}].[{viewName}], "
                    + "so its contract with this table is unknown and the staged rows are preserved",
            };
        }

        // The consolidation anchor: this flow's last successful load. No prior success means nothing this flow
        // loaded is proven delivered (a seeded or hand-filled table has no ledger entry to compare against).
        var lastLoadEnd = await catalog.Runs.AsNoTracking()
            .Where(r => r.PipelineId == pipelineId && r.Status == RunStatuses.Succeeded && r.EndUtc != null)
            .MaxAsync(r => (DateTime?)r.EndUtc, ct).ConfigureAwait(false);
        if (lastLoadEnd is null)
        {
            return new LandingReset
            {
                Authorized = false,
                Reason = "this flow has no prior successful run, so nothing staged is proven consolidated",
            };
        }

        // Delivery proof, per consumer: a successful run that STARTED after the last load ENDED saw every staged
        // row (a run that started earlier may have read a partial table, so it proves nothing). A consumer that
        // is disabled or failing blocks the reset indefinitely, loudly: growth is the correct failure mode when
        // delivery cannot be proven, silent data loss is not.
        foreach (var consumer in consumers)
        {
            var consumed = await catalog.Runs.AsNoTracking()
                .AnyAsync(r => r.PipelineId == consumer.ToPipelineId
                    && r.Status == RunStatuses.Succeeded
                    && r.StartUtc != null && r.StartUtc >= lastLoadEnd, ct).ConfigureAwait(false);
            if (!consumed)
            {
                return new LandingReset
                {
                    Authorized = false,
                    Reason = $"consumer '{consumer.ToFlow}' has no successful run after this flow's last load at {lastLoadEnd:u}",
                };
            }
        }

        return new LandingReset
        {
            Authorized = true,
            Reason = $"every consumer ({string.Join(", ", consumers.Select(c => c.ToFlow))}) completed a successful run "
                + $"after this flow's last load at {lastLoadEnd:u}",
        };
    }
}
