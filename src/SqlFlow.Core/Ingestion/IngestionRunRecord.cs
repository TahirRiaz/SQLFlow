namespace SqlFlow.Core.Ingestion;

/// <summary>
/// The per-execution record handed to an <see cref="IIngestionRunLog"/>: the V3 equivalent of one
/// flw.SysLog row. Counts are <c>long</c> (the legacy INT columns silently truncated past 2.1B rows). All
/// values are already redacted of secrets by the runner.
/// </summary>
public sealed record IngestionRunRecord
{
    public required Guid RunId { get; init; }

    public required int FlowId { get; init; }

    public string FlowType { get; init; } = "ing";

    /// <summary>The legacy Process string: <c>srcServer.srcObject--&gt;trgServer.trgObject</c>.</summary>
    public string Process { get; init; } = string.Empty;

    public string? Batch { get; init; }

    public string? SysAlias { get; init; }

    public string? ExecMode { get; init; }

    public required DateTime StartTimeUtc { get; init; }

    public required DateTime EndTimeUtc { get; init; }

    public int DurationSeconds { get; init; }

    /// <summary>Rows staged from the source (legacy Fetched).</summary>
    public long RowsFetched { get; init; }

    public long RowsInserted { get; init; }

    public long RowsUpdated { get; init; }

    public long RowsDeleted { get; init; }

    /// <summary>Rows per second (legacy FlowRate), 0 when the run was sub-second.</summary>
    public decimal FlowRate { get; init; }

    public required bool Success { get; init; }

    public int? Threads { get; init; }

    public string? SelectCmd { get; init; }

    public string? InsertCmd { get; init; }

    public string? UpdateCmd { get; init; }

    public string? CreateCmd { get; init; }

    /// <summary>The rendered, ordered SQL trace of the run (every generated statement), persisted to
    /// flw.SysLog.TraceLog in with-database mode.</summary>
    public string? TraceLog { get; init; }

    /// <summary>The run error (already redacted), null on success.</summary>
    public string? Error { get; init; }
}
