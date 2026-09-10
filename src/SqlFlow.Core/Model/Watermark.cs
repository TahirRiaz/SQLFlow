using System.Globalization;

namespace SqlFlow.Core.Model;

/// <summary>
/// The orderable category of a row-level incremental watermark column, resolved from the target column's
/// runtime type when the engine probes its MAX. The category decides how the bound is serialized into a
/// reader option, rendered as a DuckDB literal for pushdown, and compared against a streamed source cell.
/// </summary>
public enum WatermarkKind
{
    /// <summary>Whole-number column (tinyint/smallint/int/bigint). Compared as <see cref="long"/>.</summary>
    Whole,

    /// <summary>Fixed-point column (decimal/numeric/money). Compared as <see cref="decimal"/>.</summary>
    Fixed,

    /// <summary>Floating-point column (real/float). Compared as <see cref="double"/>.</summary>
    Floating,

    /// <summary>Date or datetime column (date/datetime/datetime2/smalldatetime). Compared as <see cref="DateTime"/>.</summary>
    DateTime,

    /// <summary>Timezone-aware column (datetimeoffset). Compared as <see cref="DateTimeOffset"/>.</summary>
    DateTimeOffset,

    /// <summary>Text column (char/varchar/nchar/nvarchar). Compared ordinally as <see cref="string"/>.</summary>
    Text,
}

/// <summary>
/// A resolved incremental high-water mark: the typed bound (already shifted back by any overlap window) and
/// the category that governs how it is rendered and compared. Produced by the incremental probe from the
/// target table, which is the state, so no control database is required.
/// </summary>
public sealed record WatermarkValue
{
    public required WatermarkKind Kind { get; init; }

    /// <summary>The overlap-applied bound, typed per <see cref="Kind"/> (long/decimal/double/DateTime/DateTimeOffset/string).</summary>
    public required object Value { get; init; }
}

/// <summary>
/// The single source of truth for a row-level watermark predicate: it serializes the bound into the engine's
/// reader options, renders it as an injection-safe DuckDB literal for predicate pushdown, and compares a
/// streamed source cell against it for the reader-agnostic filtering decorator. Both the pushdown path and the
/// client-side filter resolve the same comparison from the same canonical string, so they can never disagree.
/// </summary>
public static class WatermarkPredicate
{
    /// <summary>Source option carrying the watermark column name the predicate applies to.</summary>
    public const string ColumnOption = "incrementalColumn";

    /// <summary>Source option carrying the canonical (culture-invariant) serialization of the bound.</summary>
    public const string ValueOption = "incrementalValue";

    /// <summary>Source option carrying the <see cref="WatermarkKind"/> name.</summary>
    public const string KindOption = "incrementalKind";

    /// <summary>The culture-invariant serialization of a bound, round-trippable by <see cref="ParseKind"/> plus the comparison helpers.</summary>
    public static string Format(WatermarkValue bound)
    {
        ArgumentNullException.ThrowIfNull(bound);
        return bound.Kind switch
        {
            WatermarkKind.Whole => Convert.ToInt64(bound.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            WatermarkKind.Fixed => Convert.ToDecimal(bound.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            WatermarkKind.Floating => Convert.ToDouble(bound.Value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture),
            WatermarkKind.DateTime => ((DateTime)bound.Value).ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            WatermarkKind.DateTimeOffset => ((DateTimeOffset)bound.Value).ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture),
            WatermarkKind.Text => (string)bound.Value,
            _ => throw new SqlFlowException($"Unsupported watermark kind '{bound.Kind}'."),
        };
    }

    /// <summary>Parses a <see cref="WatermarkKind"/> name (case-insensitive); throws on an unknown name.</summary>
    public static WatermarkKind ParseKind(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        return Enum.TryParse<WatermarkKind>(kind, ignoreCase: true, out var parsed)
            ? parsed
            : throw new SqlFlowException($"Unknown incremental watermark kind '{kind}'.");
    }

    /// <summary>
    /// A DuckDB predicate (<c>"col" &gt; literal</c>) that keeps only rows past the bound, for pushdown into the
    /// scan. The column name is double-quote escaped and the literal is single-quote escaped, so neither the
    /// column nor the bound can break out of the expression.
    /// </summary>
    public static string DuckDbPredicate(string column, string canonicalValue, WatermarkKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        var quotedColumn = "\"" + column.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        return $"{quotedColumn} > {DuckDbLiteral(canonicalValue, kind)}";
    }

    private static string DuckDbLiteral(string canonicalValue, WatermarkKind kind)
    {
        ArgumentNullException.ThrowIfNull(canonicalValue);
        return kind switch
        {
            WatermarkKind.Whole or WatermarkKind.Fixed or WatermarkKind.Floating => canonicalValue,
            WatermarkKind.DateTime => $"TIMESTAMP '{Sql(canonicalValue.Replace('T', ' '))}'",
            WatermarkKind.DateTimeOffset => $"TIMESTAMPTZ '{Sql(canonicalValue.Replace('T', ' '))}'",
            WatermarkKind.Text => $"'{Sql(canonicalValue)}'",
            _ => throw new SqlFlowException($"Unsupported watermark kind '{kind}'."),
        };
    }

    /// <summary>
    /// True when a streamed source <paramref name="cell"/> is strictly past the bound. A null or DBNull cell is
    /// never past the bound (it is excluded), so unknown values are not resurrected on a re-run. The cell is
    /// coerced to the bound's kind, so a string-typed reader (CSV) and a typed reader (Parquet) compare the same.
    /// </summary>
    public static bool IsAfter(object? cell, string canonicalValue, WatermarkKind kind)
    {
        if (cell is null || cell is DBNull)
        {
            return false;
        }

        return kind switch
        {
            WatermarkKind.Whole => Convert.ToInt64(cell, CultureInfo.InvariantCulture) > long.Parse(canonicalValue, CultureInfo.InvariantCulture),
            WatermarkKind.Fixed => Convert.ToDecimal(cell, CultureInfo.InvariantCulture) > decimal.Parse(canonicalValue, NumberStyles.Number, CultureInfo.InvariantCulture),
            WatermarkKind.Floating => Convert.ToDouble(cell, CultureInfo.InvariantCulture) > double.Parse(canonicalValue, NumberStyles.Float, CultureInfo.InvariantCulture),
            WatermarkKind.DateTime => Convert.ToDateTime(cell, CultureInfo.InvariantCulture) > DateTime.Parse(canonicalValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            WatermarkKind.DateTimeOffset => ToOffset(cell) > DateTimeOffset.Parse(canonicalValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            WatermarkKind.Text => string.CompareOrdinal(Convert.ToString(cell, CultureInfo.InvariantCulture), canonicalValue) > 0,
            _ => throw new SqlFlowException($"Unsupported watermark kind '{kind}'."),
        };
    }

    private static DateTimeOffset ToOffset(object cell) => cell switch
    {
        DateTimeOffset dto => dto,
        DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
        _ => DateTimeOffset.Parse(Convert.ToString(cell, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
    };

    private static string Sql(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
