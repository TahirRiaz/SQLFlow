using SqlFlow.Core.Runs;

namespace SqlFlow.Core.Ingestion;

/// <summary>
/// Per-execution run parameters that are not part of the flow definition (legacy ExecMode and the batch run
/// id). Passed to the runner per call, with sensible defaults, rather than baked into the immutable flow.
/// </summary>
public sealed record IngestionRunOptions
{
    /// <summary>How the run was launched (legacy ExecMode), for the run log. Null leaves it unset.</summary>
    public string? ExecMode { get; init; }

    /// <summary>The batch run instance id (legacy BatchID), carried for the deferred SysLogBatch status.</summary>
    public string? BatchId { get; init; }

    /// <summary>Receives the run's canonical event log as it happens (a <see cref="RunLogger"/> in YAML mode,
    /// streamed live and written to the .sqlflow run folder). Null records nothing.</summary>
    public IRunEventSink? Events { get; init; }

    /// <summary>Receives each generated SQL statement as the runner executes it, so a live consumer (the node)
    /// can persist the trace to the catalog during the run. Null (the default) records nothing live; the trace
    /// still rides the result and is projected from the artifact at completion, so behavior is unchanged for
    /// every CLI run.</summary>
    public IRunStatementSink? StatementSink { get; init; }

    /// <summary>An orchestrator-assigned run id the runner stamps on the run instead of minting its own. The
    /// control-plane trigger hands a run id to its caller before the run executes, so the runner must record the
    /// run under that exact id for <c>GET /runs/{id}</c> to resolve. Null mints a fresh id, which is what every
    /// direct CLI run does, so the existing behavior is unchanged.</summary>
    public Guid? RunId { get; init; }

    /// <summary>Per-run substitution parameters (full load, backfill window): trigger-time overrides of the
    /// incremental window, applied for this run only. Defaults to <see cref="RunParameters.None"/> (no change).
    /// <see cref="RunParameters.FilePattern"/> is a file-flow concern and is ignored here.</summary>
    public RunParameters Parameters { get; init; } = RunParameters.None;

    /// <summary>The downstream (next) table the high-water MAX probe should read instead of the flow's own
    /// target, resolved from lineage by the control plane when the flow sets
    /// <c>incremental.watermarkFromDownstream</c>. Null (the default, and every direct CLI run, which has no
    /// lineage) probes the flow's own target. The resolver honors this only when it is reachable and carries
    /// every watermark column, falling back to the target otherwise.</summary>
    public RelationalObject? WatermarkSourceTable { get; init; }
}
