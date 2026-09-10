using SqlFlow.Core;
using SqlFlow.Core.Model;

namespace SqlFlow.DuckDb;

/// <summary>
/// Builds the DuckDB FROM-relation for a source spec, and exposes the reader's options. The relation is either a
/// verbatim user <c>query</c> wrapped as a subquery, or a file scan (<c>read_parquet</c> / <c>read_csv_auto</c> /
/// <c>read_json_auto</c> / <c>delta_scan</c>) over the location (a single path or a glob). Path literals are
/// single-quote escaped, so a location can never break out of the scan call. Pure text generation.
/// </summary>
public static class DuckDbQuery
{
    /// <summary>The FROM relation: a subquery for a verbatim query, else a typed scan over the location. A Delta
    /// table reads through <c>delta_scan</c>, which follows the transaction log to the current snapshot (and,
    /// with deltaVersion/deltaTimestamp, an earlier one), not the raw Parquet files.</summary>
    public static string BuildRelation(SourceSpec source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (Option(source, "query") is { Length: > 0 } query)
        {
            return $"({query}) AS _src";
        }

        var location = source.Location
            ?? throw new SqlFlowException("A duckdb source needs a 'location' (a file/glob/table path) or a 'query' option.");
        var escaped = Escape(location);

        return EffectiveFormat(source) switch
        {
            "parquet" or "prq" => $"read_parquet('{escaped}'{ParquetArgs(source)})",
            "csv" => $"read_csv_auto('{escaped}')",
            "json" or "ndjson" or "jsonl" => $"read_json_auto('{escaped}')",
            "delta" => $"delta_scan('{escaped}'{DeltaArgs(source)})",
            var other => throw new SqlFlowException($"Unknown duckdb source format '{other}'. Use parquet, csv, json, or delta."),
        };
    }

    /// <summary>The resolved format: <c>delta</c> when the source type is <c>delta</c>, else the explicit
    /// <c>format</c> option, else inferred from the location's extension (defaulting to parquet).</summary>
    public static string EffectiveFormat(SourceSpec source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.Equals(source.Type, "delta", StringComparison.OrdinalIgnoreCase))
        {
            return "delta";
        }

        var explicitFormat = Option(source, "format");
        return (string.IsNullOrWhiteSpace(explicitFormat)
            ? (source.Location is { } loc ? InferFormat(loc) : "parquet")
            : explicitFormat).ToLowerInvariant();
    }

    /// <summary>
    /// Every DuckDB extension the source needs, loaded before the scan: the user-listed ones, plus the ones the
    /// format and location imply: <c>delta</c> for a Delta table, <c>azure</c> for an <c>abfss://</c>/<c>az://</c>
    /// location, and <c>httpfs</c> for <c>s3://</c>/<c>gs://</c>/<c>http(s)://</c>. So a Delta table on ADLS
    /// reads with no manual extension wiring. De-duplicated, names validated to safe identifiers.
    /// </summary>
    public static IReadOnlyList<string> RequiredExtensions(SourceSpec source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string name)
        {
            if (seen.Add(name))
            {
                ordered.Add(name);
            }
        }

        foreach (var ext in Extensions(source))
        {
            Add(ext);
        }

        if (EffectiveFormat(source) == "delta")
        {
            Add("delta");
        }

        var location = source.Location ?? string.Empty;
        if (StartsWithScheme(location, "abfss", "abfs", "az", "azure"))
        {
            Add("azure");
        }
        else if (StartsWithScheme(location, "s3", "gs", "gcs", "r2", "http", "https"))
        {
            Add("httpfs");
        }

        return ordered;
    }

    private static bool StartsWithScheme(string location, params string[] schemes)
        => schemes.Any(s => location.StartsWith(s + "://", StringComparison.OrdinalIgnoreCase));

    /// <summary>Optional Delta time-travel arguments: a version number or an ISO timestamp pins an earlier
    /// snapshot; absent, delta_scan reads the latest.</summary>
    private static string DeltaArgs(SourceSpec source)
    {
        var version = NullIfBlank(Option(source, "deltaVersion"));
        if (version is not null)
        {
            if (!long.TryParse(version, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) || v < 0)
            {
                throw new SqlFlowException($"duckdb source 'deltaVersion' must be a non-negative whole number, got '{version}'.");
            }

            return $", version = {v.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        }

        var timestamp = NullIfBlank(Option(source, "deltaTimestamp"));
        return timestamp is not null ? $", timestamp = '{Escape(timestamp)}'" : string.Empty;
    }

    /// <summary>The extensions to INSTALL/LOAD (the <c>extensions</c> option, comma-separated), validated to safe
    /// identifiers so they cannot inject SQL.</summary>
    public static IReadOnlyList<string> Extensions(SourceSpec source)
    {
        var raw = Option(source, "extensions");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var result = new List<string>();
        foreach (var ext in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!ext.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                throw new SqlFlowException($"duckdb extension name '{ext}' is invalid; use letters, digits, and underscore.");
            }

            result.Add(ext.ToLowerInvariant());
        }

        return result;
    }

    /// <summary>Verbatim setup statements run on the connection before the query (the <c>init</c> option,
    /// semicolon-separated) for SET / CREATE SECRET and similar cloud-credential configuration. The trust
    /// boundary is the flow author, exactly like a preProcess hook.</summary>
    public static IReadOnlyList<string> InitStatements(SourceSpec source)
    {
        var raw = Option(source, "init");
        return string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    /// <summary>The optional projection (the <c>columns</c> option, comma-separated target column names); empty
    /// means every column of the relation.</summary>
    public static IReadOnlyList<string> Columns(SourceSpec source)
    {
        var raw = Option(source, "columns");
        return string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    /// <summary>The optional WHERE expression for predicate pushdown (the <c>filter</c> option).</summary>
    public static string? Filter(SourceSpec source) => NullIfBlank(Option(source, "filter"));

    /// <summary>
    /// The file-date window rendered as a predicate on the Hive partition columns, so DuckDB prunes whole partition
    /// folders at scan time instead of reading them. Applies only to a <c>fileDate.from: path</c> with
    /// <c>fileDate.hive: true</c> on a partition-aware scan (parquet or delta); a name- or modified-based date has no
    /// scan-level column to push down to and returns null. The bound dates come from the same init window and
    /// injected incremental watermark the file-store path uses, so both paths select the same slice.
    /// </summary>
    public static string? FileDatePredicate(SourceSpec source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var spec = FileDateSpec.FromOptions(source.Options);
        if (spec is null || spec.Source != FileDateSource.Path || !spec.Hive)
        {
            return null;
        }

        var format = EffectiveFormat(source);
        if (format is not ("parquet" or "delta"))
        {
            return null;
        }

        var from = ParseBound(Option(source, "initFromFileDate"));
        var to = ParseBound(Option(source, "initToFileDate"));
        var after = ParseBound(Option(source, "incrementalAfterDate"));

        // Both init-from and the incremental watermark are lower bounds; the effective lower bound is the later
        // (more restrictive) of the two. Compared at day granularity, so a partition on the boundary day is kept
        // (its later rows may be past the watermark); the keyed upsert then dedups any overlap.
        DateTime? lower = from;
        if (after is not null && (lower is null || after > lower))
        {
            lower = after;
        }

        if (lower is null && to is null)
        {
            return null;
        }

        var (startExpr, endExpr) = PartitionDateExpressions(source);

        var clauses = new List<string>();
        if (lower is not null)
        {
            clauses.Add($"{endExpr} >= DATE '{lower.Value:yyyy-MM-dd}'");
        }

        if (to is not null)
        {
            clauses.Add($"{startExpr} <= DATE '{to.Value:yyyy-MM-dd}'");
        }

        return clauses.Count == 0 ? null : string.Join(" AND ", clauses);
    }

    /// <summary>
    /// Builds the <c>[start, end]</c> date expressions for a partition from its Hive columns, widening for missing
    /// finer components: a <c>year</c>-only partition spans the whole year, a <c>year/month</c> partition the whole
    /// month. Columns are cast to integer so they compose whether DuckDB inferred them as integers or strings.
    /// </summary>
    private static (string Start, string End) PartitionDateExpressions(SourceSpec source)
    {
        var columns = PartitionColumns(source);
        var year = Int(columns.Year);

        if (columns.Day is { } dayColumn)
        {
            var day = $"make_date({year}, {Int(columns.Month!)}, {Int(dayColumn)})";
            return (day, day);
        }

        if (columns.Month is { } monthColumn)
        {
            var firstOfMonth = $"make_date({year}, {Int(monthColumn)}, 1)";
            return (firstOfMonth, $"last_day({firstOfMonth})");
        }

        return ($"make_date({year}, 1, 1)", $"make_date({year}, 12, 31)");
    }

    /// <summary>The Hive partition column names, from the <c>fileDate.partitions</c> option (default year, month,
    /// day). Names are validated to safe identifiers and only year/month/day are recognized, finest last.</summary>
    private static (string Year, string? Month, string? Day) PartitionColumns(SourceSpec source)
    {
        var raw = Option(source, "fileDate.partitions");
        var declared = string.IsNullOrWhiteSpace(raw)
            ? new[] { "year", "month", "day" }
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string? year = null, month = null, day = null;
        foreach (var name in declared)
        {
            if (!name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                throw new SqlFlowException($"fileDate.partitions column '{name}' is invalid; use letters, digits, and underscore.");
            }

            switch (name.ToLowerInvariant())
            {
                case "year": year = name; break;
                case "month": month = name; break;
                case "day": day = name; break;
                default:
                    throw new SqlFlowException($"fileDate.partitions recognizes year, month, and day; got '{name}'.");
            }
        }

        if (year is null)
        {
            throw new SqlFlowException("fileDate.partitions must include a 'year' column for partition pushdown.");
        }

        if (day is not null && month is null)
        {
            throw new SqlFlowException("fileDate.partitions lists 'day' without 'month'; a day partition needs its month.");
        }

        return (year, month, day);
    }

    private static string Int(string column) => $"CAST(\"{column.Replace("\"", "\"\"", StringComparison.Ordinal)}\" AS INTEGER)";

    private static DateTime? ParseBound(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string[] formats = ["yyyy-MM-dd", "yyyyMMdd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyyMMddHHmmss"];
        const System.Globalization.DateTimeStyles styles = System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal;

        if (DateTime.TryParseExact(value.Trim(), formats, System.Globalization.CultureInfo.InvariantCulture, styles, out var exact))
        {
            return exact;
        }

        return DateTime.TryParse(value.Trim(), System.Globalization.CultureInfo.InvariantCulture, styles, out var parsed)
            ? parsed
            : throw new SqlFlowException($"Invalid file date bound '{value}'. Use yyyy-MM-dd or a full timestamp.");
    }

    /// <summary>
    /// The row-level incremental watermark predicate for pushdown, or null when the engine injected no bound.
    /// It keeps only rows whose watermark column is past the bound, rendered as an injection-safe DuckDB
    /// expression so DuckDB skips row groups below the bound instead of scanning the whole dataset.
    /// </summary>
    public static string? IncrementalPredicate(SourceSpec source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var column = NullIfBlank(Option(source, WatermarkPredicate.ColumnOption));
        if (column is null)
        {
            return null;
        }

        var value = Option(source, WatermarkPredicate.ValueOption) ?? string.Empty;
        var kind = WatermarkPredicate.ParseKind(Option(source, WatermarkPredicate.KindOption)!);
        return WatermarkPredicate.DuckDbPredicate(column, value, kind);
    }

    /// <summary>Combines two optional WHERE expressions with AND; either side may be null/blank.</summary>
    public static string? CombineFilters(string? first, string? second)
    {
        var a = NullIfBlank(first);
        var b = NullIfBlank(second);
        if (a is null)
        {
            return b;
        }

        return b is null ? a : $"({a}) AND ({b})";
    }

    public static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string ParquetArgs(SourceSpec source)
    {
        var args = string.Empty;

        // A Hive file-date window pushes down onto the partition columns, so the scan must expose them: enable
        // hive_partitioning implicitly when fileDate reads from Hive path partitions, even if the author did not.
        var hiveFileDate = FileDateSpec.FromOptions(source.Options) is { Source: FileDateSource.Path, Hive: true };
        if (IsTrue(Option(source, "hivePartitioning")) || hiveFileDate)
        {
            args += ", hive_partitioning = true";
        }

        // union_by_name reconciles files whose columns differ in order/presence (schema drift across parts).
        if (IsTrue(Option(source, "unionByName")))
        {
            args += ", union_by_name = true";
        }

        return args;
    }

    private static string InferFormat(string location)
    {
        var ext = Path.GetExtension(location.Split('?')[0]).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "csv" or "tsv" or "txt" => "csv",
            "json" or "ndjson" or "jsonl" => "json",
            _ => "parquet",
        };
    }

    private static bool IsTrue(string? value) => string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? Option(SourceSpec source, string key)
    {
        if (source.Options is null)
        {
            return null;
        }

        foreach (var (k, v) in source.Options)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }

        return null;
    }
}
