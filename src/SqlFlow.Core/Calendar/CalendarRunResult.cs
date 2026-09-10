using SqlFlow.Core.Ingestion;

namespace SqlFlow.Core.Calendar;

/// <summary>The outcome of executing one calendar-dimension flow.</summary>
public sealed record CalendarRunResult
{
    public required Guid RunId { get; init; }

    public required int FlowId { get; init; }

    public required bool Success { get; init; }

    public DateTime StartTimeUtc { get; init; }

    public DateTime EndTimeUtc { get; init; }

    public int DurationSeconds { get; init; }

    /// <summary>The SQL the run executed, in order. Captured on success and failure alike.</summary>
    public IReadOnlyList<SqlTraceEntry> SqlTrace { get; init; } = [];

    /// <summary>Days generated for the declared range.</summary>
    public long RowsGenerated { get; init; }

    /// <summary>Rows the merge inserted into the target.</summary>
    public long RowsInserted { get; init; }

    /// <summary>Rows the merge updated because a generated value differed from the stored one.</summary>
    public long RowsUpdated { get; init; }

    /// <summary>Rows deleted because they fell outside the declared range. Only non-zero when the flow declares
    /// a narrower range than a previous run did.</summary>
    public long RowsDeleted { get; init; }

    /// <summary>Days in the range carrying an observance.</summary>
    public long ObservedDays { get; init; }

    /// <summary>True when the runner created the target table because it did not exist.</summary>
    public bool TableCreated { get; init; }

    /// <summary>The failure message (already redacted of any secret), or null on success.</summary>
    public string? Error { get; init; }
}
