using SqlFlow.Core.Ingestion;

namespace SqlFlow.Core.StoredProcedures;

/// <summary>The outcome of executing one stored-procedure flow.</summary>
public sealed record StoredProcedureRunResult
{
    public required Guid RunId { get; init; }

    public required int FlowId { get; init; }

    public required bool Success { get; init; }

    public DateTime StartTimeUtc { get; init; }

    public DateTime EndTimeUtc { get; init; }

    public int DurationSeconds { get; init; }

    /// <summary>The SQL the run executed (the EXEC statement), in execution order. Always captured, success or
    /// failure: the generated SQL is the debugging record.</summary>
    public IReadOnlyList<SqlTraceEntry> SqlTrace { get; init; } = [];

    /// <summary>Rows the procedure reported through an OUTPUT parameter named @Fetched, or 0 when it declares
    /// none. The legacy engine read the same four conventionally-named OUTPUT parameters into flw.SysLog.</summary>
    public long RowsFetched { get; init; }

    /// <summary>Rows the procedure reported through an OUTPUT parameter named @Inserted, or 0.</summary>
    public long RowsInserted { get; init; }

    /// <summary>Rows the procedure reported through an OUTPUT parameter named @Updated, or 0.</summary>
    public long RowsUpdated { get; init; }

    /// <summary>Rows the procedure reported through an OUTPUT parameter named @Deleted, or 0.</summary>
    public long RowsDeleted { get; init; }

    /// <summary>The failure message (already redacted of any secret), or null on success.</summary>
    public string? Error { get; init; }
}
