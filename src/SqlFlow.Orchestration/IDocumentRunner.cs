using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Runs;

namespace SqlFlow.Orchestration;

/// <summary>Run-time options threaded into a single document run, the subset that matters when a batch drives a
/// member (the rest of the CLI's run flags are presentation concerns).</summary>
public sealed record DocumentExecutionOptions
{
    public RunLogLevel LogLevel { get; init; } = RunLogLevel.Info;

    /// <summary>Force fresh health-check models (the hc <c>--retrain</c> switch).</summary>
    public bool Retrain { get; init; }

    /// <summary>Push a source-control snapshot to its remote (the scm default; false is <c>--no-push</c>).</summary>
    public bool ScmPush { get; init; } = true;

    /// <summary>Script and write a source-control snapshot without committing (the scm <c>--dry-run</c>).</summary>
    public bool ScmDryRun { get; init; }

    /// <summary>Where the live run log is echoed, line by line; null keeps it to the run-history file only. A
    /// single CLI run echoes to the console; a batch leaves this null so concurrent members do not interleave
    /// on the console (each member's log still lands in its own run folder).</summary>
    public Action<string>? Echo { get; init; }

    /// <summary>An orchestrator-assigned run id stamped on the run instead of the runner minting its own. The
    /// control-plane trigger hands this id back before the run executes, so the recorded run resolves at
    /// <c>GET /api/v1/runs/{id}</c>. Null mints a fresh id (every direct CLI run), so existing behavior is
    /// unchanged. Threaded into each kind's runner through the single execution path.</summary>
    public Guid? RunId { get; init; }

    /// <summary>Which flow of the document to execute, for documents that expand into more than one pipeline
    /// (an ingestion document with an embedded <c>healthCheck:</c> block derives a sibling hc flow). The node
    /// worker passes the claimed run's flow name and a batch passes each member's name, so the derived check
    /// executes from the same file as its parent load. Null (every plain CLI run) executes the document's
    /// primary flow. On an ingestion document a set name must match the flow or its derived check; anything
    /// else is refused rather than silently running the wrong flow.</summary>
    public string? FlowName { get; init; }

    /// <summary>Per-run substitution parameters (full load, backfill window, file pattern): trigger-time
    /// operational overrides applied by the engine for this run only. Defaults to
    /// <see cref="RunParameters.None"/>, which changes nothing.</summary>
    public RunParameters Parameters { get; init; } = RunParameters.None;

    /// <summary>The downstream (next) table the incremental watermark probe should read instead of the flow's
    /// own target, resolved from lineage by the control plane for a flow that opts in with
    /// <c>incremental.watermarkFromDownstream</c>. Null (the default, and every direct CLI run) leaves the probe
    /// on the flow's own target. Threaded into the ingestion runner through the single execution path.</summary>
    public RelationalObject? WatermarkSourceTable { get; init; }

    /// <summary>The control plane's consolidation verdict for a file flow's chained landing target
    /// (<c>load.resetWhenConsolidated</c>): whether every flow that directly reads the landing's typed view has
    /// completed a successful run since this flow's last successful load, so the engine may truncate the landing
    /// table before this run's load. Resolved from the catalog's lineage and run ledger by the node, which is
    /// the tier that has them; null (the default, and every direct CLI run) never resets. Threaded into the file
    /// flow runner through the single execution path.</summary>
    public LandingReset? LandingReset { get; init; }

    /// <summary>Receives each generated SQL statement as the run executes it, for live persistence (the node
    /// streams the trace into the catalog during the run). Null (the default) records nothing live, so a direct
    /// CLI run is unchanged; the trace is still written to the artifact and projected at completion.</summary>
    public IRunStatementSink? StatementSink { get; init; }

    /// <summary>Receives every canonical run event as it happens (file progress, resolved watermarks, engine
    /// decisions, stage summaries, warnings), for live persistence: the node streams them into the catalog so
    /// the run detail's Events view updates while the run executes. Null (the default, and every direct CLI run)
    /// records nothing live; the events are still collected into the run.json <c>events</c> array and projected
    /// at completion, so the artifact stays authoritative either way.</summary>
    public IFlowEventSink? EventSink { get; init; }
}

/// <summary>The uniform outcome of running one flow document, whatever its kind. The batch needs only the
/// terminal facts; the kind-specific detail already lives in the member's own run-history folder.</summary>
public sealed record DocumentRunOutcome
{
    public required string FlowName { get; init; }

    public required string FlowKind { get; init; }

    public required bool Success { get; init; }

    public string? Error { get; init; }

    public Guid RunId { get; init; }

    public string? RunDirectory { get; init; }

    public double DurationSeconds { get; init; }
}

/// <summary>
/// Runs one flow document end to end (build the runner, execute, write the run-history artifacts) and returns a
/// uniform outcome. This is the single execution pathway shared by the CLI's <c>run</c> verb and the batch
/// orchestrator, so a batch member runs through exactly the same code as a directly-invoked flow.
/// </summary>
public interface IDocumentRunner
{
    /// <param name="flowFile">The absolute path to the member flow document.</param>
    /// <param name="options">Run-time options threaded into the run (log level, retrain, scm push/dry-run).</param>
    /// <param name="ct">Cancellation for the run.</param>
    Task<DocumentRunOutcome> RunAsync(string flowFile, DocumentExecutionOptions options, CancellationToken ct = default);
}
