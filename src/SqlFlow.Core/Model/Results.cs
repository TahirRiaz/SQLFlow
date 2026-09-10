using System.Text.Json.Serialization;
using SqlFlow.Core.Ingestion;

namespace SqlFlow.Core.Model;

public enum FlowStatus
{
    Success,
    Failed,
}

/// <summary>
/// The incremental read scope a run actually computed and applied: which apply path it resolved to, the filter that
/// bounded the read, the watermark value the filter was built from, and where that watermark came from. This is the
/// audit trail for "which filter ran against the source data or the file system": the thing the engine derives at
/// run time from the target/source state, distinct from the operator-supplied backfill parameters. Null on a run
/// with no incremental surface (a stored-procedure or health-check flow). Serialized into run.json under
/// <c>result.incremental</c> and projected onto the run so the detail view can show it at a glance.
/// </summary>
public sealed record IncrementalSummary
{
    /// <summary>The read scope the run resolved to: <see cref="IncrementalModes"/> (full / incremental / backfill /
    /// init-load).</summary>
    public required string Mode { get; init; }

    /// <summary>The human-readable predicate or window that bounded the read: the source <c>WHERE</c> fragment for a
    /// relational flow, or the "files newer than ..." window for a file flow. Null for an unbounded full read.</summary>
    public string? Filter { get; init; }

    /// <summary>The resolved watermark value the filter was built from (a timestamp or key literal), null when the
    /// run took a full read or found no prior watermark. This is the result of the MAX/MIN probe.</summary>
    public string? Watermark { get; init; }

    /// <summary>Where the watermark came from, including the probed object: e.g. <c>target MAX [dbo].[Orders]</c>,
    /// <c>run log</c>, or <c>source MIN [dbo].[Orders]</c> (reprocess history). Null when there is no watermark.</summary>
    public string? WatermarkSource { get; init; }
}

/// <summary>The <see cref="IncrementalSummary.Mode"/> values, one per resolved read scope.</summary>
public static class IncrementalModes
{
    /// <summary>The run read everything the definition selects (no usable watermark, an empty target, or a forced
    /// full load).</summary>
    public const string Full = "full";

    /// <summary>The run bounded its read to data past a resolved watermark.</summary>
    public const string Incremental = "incremental";

    /// <summary>The run's scope was an operator-supplied backfill window (an explicit external bound).</summary>
    public const string Backfill = "backfill";

    /// <summary>The run replayed history through the init-load chunk plan.</summary>
    public const string InitLoad = "init-load";
}

/// <summary>One traced operation: what ran, how long it took, and whether it succeeded.</summary>
public sealed record TraceEntry
{
    public required string Operation { get; init; }
    public required double ElapsedMs { get; init; }
    public required bool Succeeded { get; init; }
    public long? Rows { get; init; }
    public string? Detail { get; init; }
}

/// <summary>The result of planning a flow: the diff, the exact SQL that would run, and a trace.</summary>
public sealed record FlowPlan
{
    public required FlowDefinition Flow { get; init; }
    public required IReadOnlyList<SourceColumn> SourceColumns { get; init; }
    public required TableSchema Desired { get; init; }
    public TableSchema? Actual { get; init; }
    public required SchemaDelta Delta { get; init; }
    public required IReadOnlyList<string> DdlStatements { get; init; }
    public IReadOnlyList<TraceEntry> Trace { get; init; } = [];
}

/// <summary>
/// The result of executing a flow. Always carries a <see cref="Trace"/> of the main operations and
/// their durations, so a caller can see what the engine did and exactly where it failed.
/// </summary>
public sealed record FlowResult
{
    /// <summary>Unique identifier for this run; matches the RunId stamped on the run's events.</summary>
    public Guid RunId { get; init; }

    /// <summary>Stable identity of the flow definition (same across runs); matches events' FlowId.</summary>
    public Guid FlowId { get; init; }

    public required string FlowName { get; init; }
    public FlowStatus Status { get; init; } = FlowStatus.Success;
    public long RowsLoaded { get; init; }

    /// <summary>Rows inserted into the target. The file path loads with <c>SqlBulkCopy</c>, which is insert-only
    /// (there is no keyed upsert on this path), so every loaded row is an insert and this equals
    /// <see cref="RowsLoaded"/>. Surfaced so the run's insert count is projected into the catalog and shown in the
    /// GUI alongside the relational runner's counts; updated/deleted do not apply to a file load and stay absent.</summary>
    public long RowsInserted { get; init; }

    /// <summary>The schema-diff DDL the run applied (create table / add columns). An in-process summary field
    /// (the CLI's "N DDL statement(s)" line); it is a strict subset of <see cref="SqlTrace"/>, so it is kept out
    /// of the run.json artifact to avoid the completion projection counting the same schema statements twice
    /// (once from the trace, once from here).</summary>
    [JsonIgnore]
    public IReadOnlyList<string> DdlExecuted { get; init; } = [];

    /// <summary>Every SQL statement the run executed against the target, in execution order: the schema DDL, the
    /// pre/post-process DDL, and the transformation-view refresh. This is the file flow's statement trace, the
    /// same contract the ingestion/export/sp/hc runners expose, so it streams live to the node's statement sink
    /// during the run and is the authoritative record projected from the artifact at completion.</summary>
    public IReadOnlyList<SqlTraceEntry> SqlTrace { get; init; } = [];

    /// <summary>The files processed by this run (the in-result "file log" for stateless mode).</summary>
    public IReadOnlyList<ProcessedFile> ProcessedFiles { get; init; } = [];

    public IReadOnlyList<TraceEntry> Trace { get; init; } = [];
    public double TotalMs { get; init; }
    public string? Error { get; init; }

    /// <summary>
    /// The typed transformation view generated over the loaded table as the run's post-process (null when the
    /// flow does not generate one). Carries the resolved per-column projection - the detected/declared transforms
    /// the catalog records for the pipeline - and the exact DDL that ran.
    /// </summary>
    public TransformViewResult? TransformView { get; init; }

    /// <summary>The incremental read scope this run computed and applied (the watermark-derived filter, or the
    /// full-read decision), null when the flow has no incremental spec. Surfaced so the run detail can show which
    /// filter ran against the file system without hunting through the log.</summary>
    public IncrementalSummary? Incremental { get; init; }

    /// <summary>How <c>DataSet_DW</c> was derived for this run (e.g. <c>filename dates; month-first (inferred from
    /// file set)</c> or <c>last-modified</c>), null for a flow that emits no <c>DataSet_DW</c>. Serialized into
    /// run.json under <c>result.dataSetConvention</c> and projected onto the run so the detail view shows what the
    /// reader detected.</summary>
    public string? DataSetConvention { get; init; }
}

/// <summary>
/// The outcome of the transformation-view post-process: the view refreshed over the flow's loaded table and the
/// resolved column projection it exposes (authored transforms merged with inferred types; the downstream chained
/// flow reads this view to get correctly-typed data).
/// </summary>
public sealed record TransformViewResult
{
    /// <summary>The view's unqualified name (<c>v&lt;Table&gt;</c>), in the loaded table's schema and database.</summary>
    public required string ViewName { get; init; }

    /// <summary>The exact <c>CREATE OR ALTER VIEW</c> statement the run executed.</summary>
    public required string Ddl { get; init; }

    /// <summary>The resolved, ordered projection the view exposes (one entry per output column).</summary>
    public required IReadOnlyList<InferredColumn> Columns { get; init; }
}
