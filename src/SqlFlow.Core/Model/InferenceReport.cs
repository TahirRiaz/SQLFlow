namespace SqlFlow.Core.Model;

/// <summary>
/// The output of the independent inference process: the optimal type per column, the full transform
/// SELECT you can run against the table, and (when validated) a per-column sanity check. Serializes to
/// JSON - viewed directly (no metadata DB) or stored into the pre-ingestion transform metadata.
/// </summary>
public sealed record InferenceReport
{
    public required string Schema { get; init; }
    public required string Table { get; init; }
    public required string OnConvertError { get; init; }

    /// <summary>The locale the run bound to (the server's, or an explicit override) - e.g. "nb-NO".</summary>
    public required string Culture { get; init; }

    /// <summary>The date-component ordering applied to every column (Ymd / Dmy / Mdy).</summary>
    public required string DateOrder { get; init; }

    public required IReadOnlyList<InferredColumnReport> Columns { get; init; }

    /// <summary>
    /// A runnable SELECT that applies every column's inferred conversion against the table. Useful to
    /// eyeball or execute to confirm the conversions before committing to the typed schema.
    /// </summary>
    public required string TransformSelect { get; init; }

    /// <summary>
    /// Per-column sanity check of the conversions against the actual data; null when validation was not
    /// run. Flags columns where non-null values would silently become NULL under the inferred type.
    /// </summary>
    public InferenceValidation? Validation { get; init; }
}

public sealed record InferredColumnReport
{
    public required string ColumnName { get; init; }
    public required string DataType { get; init; }
    public required string SelectExpression { get; init; }
    public bool Converted { get; init; }
    public int? Style { get; init; }

    /// <summary>Numeric convention used to normalize the column (Locale / Invariant); null if non-numeric.</summary>
    public string? NumericFormat { get; init; }

    public long Sampled { get; init; }
    public long Total { get; init; }
}

/// <summary>Result of running the inferred conversions against the table to sanity-check them.</summary>
public sealed record InferenceValidation
{
    /// <summary>True when no converted column would silently null out any non-null value.</summary>
    public required bool IsValid { get; init; }

    public required IReadOnlyList<ColumnValidation> Columns { get; init; }
}

/// <summary>How well an inferred type fits a column's actual data.</summary>
public sealed record ColumnValidation
{
    public required string ColumnName { get; init; }
    public required string DataType { get; init; }

    /// <summary>Non-null, non-empty values evaluated.</summary>
    public long NonNull { get; init; }

    /// <summary>Non-null values that the inferred conversion turns into NULL (data loss). Flagged.</summary>
    public long SilentNulls { get; init; }

    /// <summary>Percentage of evaluated values that convert cleanly (100 when nothing is lost).</summary>
    public double FitPercent { get; init; }

    /// <summary>
    /// ok (converts cleanly), lossy (some values would silently null out - flagged), kept-string (no
    /// conversion applied), or empty (no values to evaluate).
    /// </summary>
    public required string Status { get; init; }
}

/// <summary>A converted column to verify: its inferred type, optional date style, and numeric convention.</summary>
public sealed record ConversionCheck
{
    public required string ColumnName { get; init; }
    public required string DataType { get; init; }
    public int? Style { get; init; }

    /// <summary>
    /// The numeric convention to normalize the column with before converting; null for non-numeric
    /// columns. Ensures the validation reproduces exactly the conversion the transform emits.
    /// </summary>
    public NumericFormat? NumericFormat { get; init; }
}

/// <summary>The raw counts from running a conversion check against the table.</summary>
public sealed record ConversionCheckResult
{
    public required string ColumnName { get; init; }
    public long NonNull { get; init; }
    public long SilentNulls { get; init; }
}
