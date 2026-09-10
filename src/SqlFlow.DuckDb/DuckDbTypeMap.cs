using System.Globalization;

namespace SqlFlow.DuckDb;

/// <summary>The SQL Server mapping of one DuckDB result column: the CLR type the value arrives as, the target
/// SQL Server type text, and whether DuckDB reported a nested type (list/struct/map) that must be projected as
/// JSON text rather than copied directly.</summary>
public sealed record DuckDbColumnType
{
    public required Type ClrType { get; init; }
    public required string SqlType { get; init; }
    public int? Precision { get; init; }
    public int? Scale { get; init; }
    public bool IsNested { get; init; }
}

/// <summary>
/// Maps a DuckDB logical type (the text DuckDB's <c>DESCRIBE</c> reports, e.g. <c>BIGINT</c>,
/// <c>DECIMAL(18,2)</c>, <c>TIMESTAMP WITH TIME ZONE</c>, <c>INTEGER[]</c>, <c>STRUCT(...)</c>) to the SQL Server
/// type that will hold it. Unsigned widths widen to the next signed type that holds them; a nested type
/// (list/struct/map/union) is flagged so the reader projects it as JSON text into <c>nvarchar(max)</c> (the same
/// keep-a-subtree-as-a-string behavior the Parquet reader uses). All columns are treated as nullable, because
/// schema evolution across files null-fills a column missing from some of them.
/// </summary>
public static class DuckDbTypeMap
{
    public static DuckDbColumnType Map(string duckType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(duckType);
        var type = duckType.Trim();
        var upper = type.ToUpperInvariant();

        // Nested types: a list/array ends with ']'; struct/map/union start with their keyword. Projected as JSON.
        if (upper.EndsWith("[]", StringComparison.Ordinal)
            || upper.StartsWith("STRUCT(", StringComparison.Ordinal)
            || upper.StartsWith("MAP(", StringComparison.Ordinal)
            || upper.StartsWith("UNION(", StringComparison.Ordinal))
        {
            return new DuckDbColumnType { ClrType = typeof(string), SqlType = "nvarchar(max)", IsNested = true };
        }

        if (upper.StartsWith("DECIMAL", StringComparison.Ordinal) || upper.StartsWith("NUMERIC", StringComparison.Ordinal))
        {
            var (precision, scale) = ParseDecimal(upper);
            var p = Math.Clamp(precision, 1, 38);
            var s = Math.Clamp(scale, 0, p);
            return new DuckDbColumnType { ClrType = typeof(decimal), SqlType = $"decimal({p},{s})", Precision = p, Scale = s };
        }

        // VARCHAR(n) keeps its length; bare VARCHAR/TEXT is unbounded.
        if (upper.StartsWith("VARCHAR(", StringComparison.Ordinal))
        {
            var len = ParseLength(upper);
            return new DuckDbColumnType { ClrType = typeof(string), SqlType = len is > 0 and <= 4000 ? $"nvarchar({len})" : "nvarchar(max)" };
        }

        return upper switch
        {
            "BOOLEAN" or "BOOL" or "LOGICAL" => new() { ClrType = typeof(bool), SqlType = "bit" },
            "TINYINT" or "INT1" => new() { ClrType = typeof(short), SqlType = "smallint" },          // DuckDB TINYINT is signed
            "UTINYINT" => new() { ClrType = typeof(byte), SqlType = "tinyint" },
            "SMALLINT" or "INT2" or "SHORT" => new() { ClrType = typeof(short), SqlType = "smallint" },
            "USMALLINT" => new() { ClrType = typeof(int), SqlType = "int" },
            "INTEGER" or "INT" or "INT4" or "SIGNED" => new() { ClrType = typeof(int), SqlType = "int" },
            "UINTEGER" => new() { ClrType = typeof(long), SqlType = "bigint" },
            "BIGINT" or "INT8" or "LONG" => new() { ClrType = typeof(long), SqlType = "bigint" },
            "UBIGINT" => new() { ClrType = typeof(decimal), SqlType = "decimal(20,0)", Precision = 20, Scale = 0 },
            "HUGEINT" or "UHUGEINT" or "INT128" => new() { ClrType = typeof(decimal), SqlType = "decimal(38,0)", Precision = 38, Scale = 0 },
            "FLOAT" or "REAL" or "FLOAT4" => new() { ClrType = typeof(float), SqlType = "real" },
            "DOUBLE" or "FLOAT8" => new() { ClrType = typeof(double), SqlType = "float" },
            "VARCHAR" or "TEXT" or "STRING" or "CHAR" or "BPCHAR" => new() { ClrType = typeof(string), SqlType = "nvarchar(max)" },
            "BLOB" or "BYTEA" or "BINARY" or "VARBINARY" => new() { ClrType = typeof(byte[]), SqlType = "varbinary(max)" },
            "DATE" => new() { ClrType = typeof(DateTime), SqlType = "date" },
            "TIME" => new() { ClrType = typeof(TimeSpan), SqlType = "time" },
            "TIMESTAMP" or "DATETIME" or "TIMESTAMP_S" or "TIMESTAMP_MS" or "TIMESTAMP_NS" or "TIMESTAMP_US"
                => new() { ClrType = typeof(DateTime), SqlType = "datetime2" },
            "TIMESTAMP WITH TIME ZONE" or "TIMESTAMPTZ" => new() { ClrType = typeof(DateTimeOffset), SqlType = "datetimeoffset" },
            "UUID" => new() { ClrType = typeof(Guid), SqlType = "uniqueidentifier" },
            // BIT (bitstring), INTERVAL, JSON, ENUM and anything else: carry as text. JSON is already JSON text.
            _ => new() { ClrType = typeof(string), SqlType = "nvarchar(max)" },
        };
    }

    private static (int Precision, int Scale) ParseDecimal(string type)
    {
        var open = type.IndexOf('(', StringComparison.Ordinal);
        if (open < 0)
        {
            return (18, 3); // DuckDB's default DECIMAL is DECIMAL(18,3)
        }

        var close = type.IndexOf(')', open);
        var inner = type[(open + 1)..close];
        var parts = inner.Split(',', StringSplitOptions.TrimEntries);
        var precision = int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 18;
        var scale = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
        return (precision, scale);
    }

    private static int? ParseLength(string type)
    {
        var open = type.IndexOf('(', StringComparison.Ordinal);
        var close = type.IndexOf(')', open + 1);
        if (open < 0 || close < 0)
        {
            return null;
        }

        return int.TryParse(type[(open + 1)..close], NumberStyles.Integer, CultureInfo.InvariantCulture, out var len) ? len : null;
    }
}
