namespace SqlFlow.Core.Ingestion;

/// <summary>
/// The outcome of one assertion: the materialized SQL and the first row's first two columns (legacy Result /
/// AssertedValue). On a per-assertion failure <see cref="Evaluated"/> is false and <see cref="Result"/> is the
/// string "0" (the legacy sentinel), with the error captured; the other assertions still run. This is log-only
/// by default: an assertion outcome never fails or rolls back the load (legacy parity).
/// </summary>
public sealed record AssertionResult
{
    public required string Name { get; init; }

    public required string MaterializedSql { get; init; }

    /// <summary>First column of the first row as a string ("" for zero rows or NULL); "0" on evaluation error.</summary>
    public string Result { get; init; } = string.Empty;

    /// <summary>Second column of the first row as a string ("" when absent, NULL, or on error).</summary>
    public string AssertedValue { get; init; } = string.Empty;

    public bool Evaluated { get; init; }

    public string? Error { get; init; }

    public TimeSpan Duration { get; init; }
}
