namespace SqlFlow.Core.Runs;

/// <summary>
/// The control plane's per-run consolidation verdict for a chained landing (bronze) target, resolved from the
/// catalog's lineage and run ledger by the node and threaded into the engine, which has no catalog of its own.
/// The contract is one hop only: the landing table may be reset when every flow that DIRECTLY reads its typed
/// view has completed a successful run after this flow's last successful load, proving the staged rows were
/// delivered to the next phase (silver). Whether anything further downstream (gold) ran is deliberately not
/// part of the verdict. Null (a direct CLI run, or a flow with no consumers in the lineage graph) means no
/// verdict was computed and the engine leaves the target alone.
/// </summary>
public sealed record LandingReset
{
    /// <summary>Whether every direct consumer has consolidated the landing table since its last load. The
    /// engine truncates the landing target at the start of the run only when this is true (and the flow's own
    /// gates hold: append mode, <c>load.resetWhenConsolidated</c>, a generated typed view, a pre-existing
    /// target, and a source read that actually found files).</summary>
    public required bool Authorized { get; init; }

    /// <summary>Why the verdict is what it is, in operator terms: which consumer runs proved delivery, or which
    /// consumer has not caught up. Emitted on the run's event stream either way, so a landing table that is
    /// growing has a loud, queryable explanation on every run.</summary>
    public required string Reason { get; init; }
}
