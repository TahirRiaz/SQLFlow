using System.Data.Common;
using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Core.Catalog;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Runs;
using SqlFlow.SqlServer.Schema;

namespace SqlFlow.SqlServer.Ingestion;

/// <summary>The computed incremental window: the source WHERE fragment to append to <c>WHERE 1=1</c> (it
/// begins with <c>" AND "</c> or is empty), and whether the run should take the full-load / insert-all apply
/// path (an empty or absent target, or a keyless flow with no predicate).</summary>
public sealed record IncrementalWindow
{
    public required string SourceWhere { get; init; }

    public required bool RunFullLoad { get; init; }

    /// <summary>The MAX watermark probe run against the target, surfaced for the run's SQL trace; null when
    /// no probe ran.</summary>
    public string? TargetMaxProbeSql { get; init; }

    /// <summary>The MIN watermark probe run against the source (FetchMinValuesFromSource), null when none ran.</summary>
    public string? SourceMinProbeSql { get; init; }

    /// <summary>The resolved watermark values the source WHERE was built from, rendered as
    /// <c>column: value</c> pairs; null when no probe bounded the read (full load / empty target). This is the
    /// result of the MAX (or MIN, on reprocess) probe, surfaced for the run detail.</summary>
    public string? Watermark { get; init; }

    /// <summary>Where the resolved watermark came from, including the probed object: <c>target MAX
    /// [schema].[table]</c> or <c>source MIN [schema].[table]</c>. Null when there is no watermark.</summary>
    public string? WatermarkSource { get; init; }
}

/// <summary>
/// Resolves the incremental read window for a relational flow, porting the legacy CommonDB.GetIncWhereExp +
/// ProcessIngestion watermark assembly. It probes MAX over the TARGET (and, when FetchMinValuesFromSource,
/// MIN over the SOURCE), then builds the source WHERE following the exact legacy precedence: a replace-filter
/// wins, then the FullLoad flag, then an empty/absent target, then the incremental-column predicate, then the
/// date predicate, with the append-filter concatenated last. The date watermark is moved back OverlapDays
/// days. Watermark literals are formatted from the introspected source column types (numeric, binary, date,
/// string) rather than re-deriving types inline. The legacy WITH(hint) and the Synapse FOR-XML shaping are
/// deliberately not ported (SQL Server target only).
/// </summary>
public sealed class IncrementalWindowResolver
{
    private static readonly Watermark[] NoMarks = [];

    private readonly ICatalogReader _catalog;
    private readonly IConnectionFactory _factory;

    public IncrementalWindowResolver(ICatalogReader catalog, IConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(factory);
        _catalog = catalog;
        _factory = factory;
    }

    public async Task<IncrementalWindow> ResolveAsync(
        IngestionFlow flow,
        ResolvedConnection source,
        string targetConnectionString,
        IReadOnlyList<SqlColumn> sourceColumns,
        ISourceSqlDialect? sourceDialect = null,
        RunParameters? parameters = null,
        RelationalObject? watermarkTable = null,
        CancellationToken ct = default)
    {
        sourceDialect ??= new SqlServerSourceDialect();
        parameters ??= RunParameters.None;
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceColumns);

        var keyless = flow.Load.KeyColumns.Count == 0;

        // Per-run substitution parameters replace the probed watermark entirely: an explicit operator bound is
        // authoritative, so no probe runs and the target's state never narrows it. The legacy filter precedence
        // in AssembleWhere still applies (a replace-filter wins even over an external window, matching how the
        // FullLoad flag has always behaved).
        if (parameters.FullLoad)
        {
            var fullWhere = AssembleWhere(flow.Source, fullLoadFlag: true, string.Empty, string.Empty, targetEmpty: true);

            // A full load already ignores the watermark, so a source filter combined with it is not contradictory:
            // it means "ignore the watermark, and read this slice of the source". Honoring it here keeps the two
            // parameters composable instead of silently dropping one.
            if (!string.IsNullOrWhiteSpace(parameters.SourceFilter))
            {
                fullWhere += " " + parameters.SourceFilter.Trim();
            }

            return new IncrementalWindow
            {
                SourceWhere = fullWhere,
                // Keyed flows still take the upsert apply (idempotent reload); only a keyless full read is the
                // insert-all path, exactly as an empty-target full load behaves.
                RunFullLoad = keyless,
            };
        }

        // An operator-supplied bound (a date window, a raw predicate, or both) replaces the probed watermark
        // entirely, so the slice asked for is read whatever the target's high-water mark says. The two compose:
        // a window narrows the declared date column, a source filter narrows anything else (a surrogate key, a
        // status flag), and either alone is enough. The date-column requirement therefore applies ONLY to the
        // window; a source filter needs no incremental declaration at all, which is what lets a flow with no
        // usable date column still be backfilled.
        var runtimePredicate = string.Empty;

        if (parameters.BackfillFrom is { } externalFrom)
        {
            var dateColumn = flow.Incremental.DateColumn;
            if (string.IsNullOrWhiteSpace(dateColumn))
            {
                throw new SqlFlowException(
                    "A backfill window needs incremental.dateColumn on the flow, so the engine knows which column to bound. " +
                    "To bound a run on a column the flow does not declare (a surrogate key, for example), use a source filter.");
            }

            var types = sourceColumns.ToDictionary(c => c.Name, c => c.DataType, StringComparer.OrdinalIgnoreCase);
            var columnType = TypeFor(types, dateColumn);
            if (columnType.BaseType.ToLowerInvariant() is not ("date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset"))
            {
                throw new SqlFlowException(
                    $"A backfill window bounds incremental.dateColumn '{dateColumn}', but its source type is '{columnType.BaseType}', not a date type.");
            }

            var quoted = sourceDialect.QuoteIdentifier(dateColumn);
            // The bound is formatted directly through the source dialect's temporal literal, so it never depends
            // on the parameter value's CLR type (unlike the watermark path, which formats an introspected value).
            runtimePredicate = $" AND {quoted} >= {BackfillLiteral(externalFrom, columnType.BaseType, sourceDialect)}";
            if (parameters.BackfillTo is { } externalTo)
            {
                runtimePredicate += $" AND {quoted} < {BackfillLiteral(externalTo, columnType.BaseType, sourceDialect)}";
            }
        }

        if (!string.IsNullOrWhiteSpace(parameters.SourceFilter))
        {
            // Appended verbatim, in the SOURCE's dialect: the fragment is composed into the source SELECT, not
            // the target write, so its identifiers and functions are the source's and one parameter serves every
            // relational provider. RunParameters.Validate has already established it is a predicate continuation
            // (leading AND/OR, no statement terminator, no comment marker), which is the trust boundary for it.
            runtimePredicate += " " + parameters.SourceFilter.Trim();
        }

        if (runtimePredicate.Length > 0)
        {
            return new IncrementalWindow
            {
                SourceWhere = AssembleWhere(flow.Source, fullLoadFlag: false, string.Empty, runtimePredicate, targetEmpty: false),
                RunFullLoad = false,
            };
        }
        var incExp = string.Empty;
        var dateExp = string.Empty;
        bool targetEmpty;
        string? maxProbeSql = null;
        string? minProbeSql = null;
        string? watermark = null;
        string? watermarkSource = null;

        if (!flow.Incremental.IsIncremental)
        {
            // No incremental configuration: read the whole source (optionally filtered). A keyless flow with
            // no predicate is a full-load apply (insert-all); a keyed flow upserts.
            targetEmpty = keyless;
        }
        else
        {
            var typesByName = sourceColumns.ToDictionary(c => c.Name, c => c.DataType, StringComparer.OrdinalIgnoreCase);
            var marks = BuildMarkList(flow, typesByName);

            // Choose the object the high-water MAX is read from. By default the control plane resolves the next
            // durable table downstream in the lineage graph (the ods/silver table this flow feeds) and hands it in:
            // its MAX is the end-to-end high-water mark, so deleting rows there re-opens the window and the source
            // is re-pulled. Only when there is no such table (no lineage, an ambiguous chain, or a direct CLI run)
            // is the flow's own target probed. The check is column-safe and reachability-safe, so a renamed/absent
            // column or an unreachable table silently falls back to the target rather than erroring or forcing a
            // full reload.
            var probeObject = flow.Target.Table;
            var probeLabel = "target";
            if (watermarkTable is not null
                && await DownstreamCarriesMarksAsync(watermarkTable, targetConnectionString, marks, ct).ConfigureAwait(false))
            {
                probeObject = watermarkTable;
                probeLabel = "downstream";
            }

            var (targetExists, maxMarks, probeSql) = await ProbeMaxAsync(flow, probeObject, targetConnectionString, marks, ct).ConfigureAwait(false);
            maxProbeSql = probeSql;
            if (!targetExists || maxMarks.All(m => m.Value is null))
            {
                // Absent or empty target: nothing to bound by, full load.
                targetEmpty = true;
            }
            else
            {
                targetEmpty = false;
                var effective = maxMarks;
                var op = ">";
                watermarkSource = $"{probeLabel} MAX {SchemaQualified(probeObject)}";

                // The flow's declared fetchMinValuesFromSource, OR a per-run override (a group backfill sets this on
                // the anchor's descendants so back-dated rows already in the source are re-pulled instead of being
                // filtered out below the target's high-water mark).
                if (flow.Incremental.FetchMinValuesFromSource || parameters.ReprocessFromSourceMin)
                {
                    var (minMarks, minSql) = await ProbeMinAsync(flow, source, marks, sourceDialect, ct).ConfigureAwait(false);
                    minProbeSql = minSql;
                    // The DECLARED fetchMinValuesFromSource widens only when the source genuinely holds older data
                    // than the target (the legacy optimization): a plain re-run does not re-read everything. But an
                    // EXPLICIT run override (an operator backfill) always reads from the source minimum, because the
                    // operator asked to reprocess: it must not silently fall back to the target's high-water mark just
                    // because an upstream flow re-landed the slice with fresh values. Both still require the source to
                    // actually hold a value (an empty source has no minimum to bound by).
                    var useSourceMin = parameters.ReprocessFromSourceMin
                        ? minMarks.Any(m => m.Value is not null)
                        : SourceMinIsLess(minMarks, maxMarks);
                    if (useSourceMin)
                    {
                        // Reprocess: bound the read at the source minimum (inclusive) instead of the target maximum.
                        effective = minMarks;
                        op = ">=";
                        watermarkSource = $"source MIN {sourceDialect.QualifyObject(flow.Source.Table)}";
                    }
                }

                (incExp, dateExp) = BuildPredicates(effective, op, sourceDialect);
                watermark = DescribeMarks(effective);
            }
        }

        var runFullLoad = targetEmpty || (incExp.Length == 0 && dateExp.Length == 0 && keyless);
        var sourceWhere = AssembleWhere(flow.Source, flow.Incremental.FullLoad, incExp, dateExp, targetEmpty);

        // The resolved watermark is reported only when its predicate is actually the bound applied to the read:
        // a replace-filter (a non-append user filter) or the declared full-load flag discards it, exactly as
        // AssembleWhere does, so the run detail never shows a watermark the read did not honor.
        var filterOverrides = !string.IsNullOrWhiteSpace(flow.Source.Filter) && !flow.Source.FilterIsAppend;
        var predicateApplied = !filterOverrides && !flow.Incremental.FullLoad && (incExp.Length > 0 || dateExp.Length > 0);
        return new IncrementalWindow
        {
            SourceWhere = sourceWhere,
            RunFullLoad = runFullLoad,
            TargetMaxProbeSql = maxProbeSql,
            SourceMinProbeSql = minProbeSql,
            Watermark = predicateApplied ? watermark : null,
            WatermarkSource = predicateApplied ? watermarkSource : null,
        };
    }

    /// <summary>Renders the resolved (non-null) watermark values as <c>column: value</c> pairs for display; the
    /// invariant string form of each probed value, so the run detail shows exactly what the MAX/MIN probe returned.</summary>
    private static string? DescribeMarks(IReadOnlyList<Watermark> marks)
    {
        var parts = marks
            .Where(m => m.Value is not null)
            .Select(m => $"{m.Column}: {FormatValue(m.Value!)}")
            .ToList();
        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    private static string FormatValue(object value) => value switch
    {
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture),
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    // Precedence is load-bearing and matches legacy ProcessIngestion exactly: replace-filter, then the
    // FullLoad flag, then empty target, then the incremental predicate, then the date predicate; the
    // append-filter is concatenated last. The user filter is raw SQL carrying its own leading keyword (the
    // legacy contract: "Example AND SystemID = 13"); it is not normalized here.
    private static string AssembleWhere(IngestionSource sourceSpec, bool fullLoadFlag, string incExp, string dateExp, bool targetEmpty)
    {
        var filter = (sourceSpec.Filter ?? string.Empty).Trim();
        var hasFilter = filter.Length > 0;

        string sourceWhere;
        if (hasFilter && !sourceSpec.FilterIsAppend)
        {
            sourceWhere = " " + filter;
        }
        else if (fullLoadFlag || targetEmpty)
        {
            sourceWhere = string.Empty;
        }
        else if (incExp.Length > 0)
        {
            sourceWhere = incExp;
        }
        else if (dateExp.Length > 0)
        {
            sourceWhere = dateExp;
        }
        else
        {
            sourceWhere = string.Empty;
        }

        if (sourceSpec.FilterIsAppend && hasFilter)
        {
            sourceWhere += " " + filter;
        }

        return sourceWhere;
    }

    private async Task<(bool Exists, IReadOnlyList<Watermark> Marks, string? Sql)> ProbeMaxAsync(
        IngestionFlow flow, RelationalObject probeObject, string targetConnectionString, IReadOnlyList<Watermark> marks, CancellationToken ct)
    {
        await using var connection = new SqlConnection(targetConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var live = await _catalog.IntrospectObjectAsync(connection, ToName(probeObject), ct).ConfigureAwait(false);
        if (live is null)
        {
            return (false, NoMarks, null);
        }

        if (marks.Count == 0)
        {
            return (true, NoMarks, null);
        }

        // A date mark is moved back OverlapDays; a numeric mark is moved back Lookback. Both shifts happen
        // inside the probe so the value the run reports as its watermark is the value the read was actually
        // bounded by. The shift is applied to the aggregate, not the column, so it stays sargable-free of the
        // source and costs nothing.
        var projections = marks.Select(m => m.IsDate
            ? $"DATEADD(day, -{flow.Incremental.OverlapDays}, MAX([{Escape(m.Column)}])) AS [{Escape(m.Column)}]"
            : NumericLookback(flow, m) is { } back
                ? $"MAX([{Escape(m.Column)}]) - {back.ToString(CultureInfo.InvariantCulture)} AS [{Escape(m.Column)}]"
                : $"MAX([{Escape(m.Column)}]) AS [{Escape(m.Column)}]");
        var sql = $"SELECT {string.Join(", ", projections)} FROM {SchemaQualified(probeObject)} WHERE 1=1{Clause(flow.Source.IncrementalClause)};";

        return (true, await ReadMarksAsync(connection, sql, marks, ct).ConfigureAwait(false), sql);
    }

    /// <summary>Whether the resolved downstream (silver) table is a safe object to probe the watermark from: it
    /// exists on the target connection AND carries every watermark column by name. A downstream table that is
    /// unreachable/absent (introspection returns null) or that renamed/dropped a watermark column (a typed view's
    /// output alias differs from the source column) returns false, so the caller falls back to the flow's own
    /// target rather than probing a MAX over a column that is not there (which would fail the run) or reading an
    /// absent object (which would be mistaken for an empty target and force a full reload).</summary>
    private async Task<bool> DownstreamCarriesMarksAsync(
        RelationalObject downstream, string targetConnectionString, IReadOnlyList<Watermark> marks, CancellationToken ct)
    {
        if (marks.Count == 0)
        {
            return false;
        }

        await using var connection = new SqlConnection(targetConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var live = await _catalog.IntrospectObjectAsync(connection, ToName(downstream), ct).ConfigureAwait(false);
        if (live is null)
        {
            return false;
        }

        var columns = new HashSet<string>(live.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        return marks.All(m => columns.Contains(m.Column));
    }

    private async Task<(IReadOnlyList<Watermark> Marks, string? Sql)> ProbeMinAsync(
        IngestionFlow flow, ResolvedConnection source, IReadOnlyList<Watermark> marks, ISourceSqlDialect dialect, CancellationToken ct)
    {
        if (marks.Count == 0)
        {
            return (NoMarks, null);
        }

        await using var connection = await _factory.OpenAsync(source, ct).ConfigureAwait(false);

        // The date watermark gets the same overlap subtraction as the MAX probe, and a numeric watermark the
        // same lookback, so the source MIN and target MAX are shifted equally and the strict-less comparison
        // is symmetric (legacy applies DATEADD to both).
        // This SQL runs ON THE SOURCE, so identifiers and date arithmetic come from the source dialect.
        var projections = marks.Select(m => m.IsDate
            ? $"{dialect.DateSubtractDays($"MIN({dialect.QuoteIdentifier(m.Column)})", flow.Incremental.OverlapDays)} AS {dialect.QuoteIdentifier(m.Column)}"
            : NumericLookback(flow, m) is { } back
                ? $"MIN({dialect.QuoteIdentifier(m.Column)}) - {back.ToString(CultureInfo.InvariantCulture)} AS {dialect.QuoteIdentifier(m.Column)}"
                : $"MIN({dialect.QuoteIdentifier(m.Column)}) AS {dialect.QuoteIdentifier(m.Column)}");
        var sql = $"SELECT {string.Join(", ", projections)} FROM {dialect.QualifyObject(flow.Source.Table)} WHERE 1=1{Clause(flow.Source.IncrementalClause)};";
        return (await ReadMarksAsync(connection, sql, marks, ct).ConfigureAwait(false), sql);
    }

    private static async Task<IReadOnlyList<Watermark>> ReadMarksAsync(DbConnection connection, string sql, IReadOnlyList<Watermark> marks, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 0;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var result = new List<Watermark>(marks.Count);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            for (var i = 0; i < marks.Count; i++)
            {
                var value = await reader.IsDBNullAsync(i, ct).ConfigureAwait(false) ? null : reader.GetValue(i);
                result.Add(marks[i] with { Value = value });
            }
        }
        else
        {
            foreach (var mark in marks)
            {
                result.Add(mark with { Value = null });
            }
        }

        return result;
    }

    // The predicates are appended to the SOURCE read, so identifier quoting and binary literals come from
    // the source dialect.
    private static (string IncExp, string DateExp) BuildPredicates(IReadOnlyList<Watermark> marks, string op, ISourceSqlDialect dialect)
    {
        var incParts = new List<string>();
        var dateParts = new List<string>();
        foreach (var mark in marks)
        {
            if (mark.Value is null)
            {
                continue;
            }

            var predicate = $"{dialect.QuoteIdentifier(mark.Column)} {op} {FormatLiteral(mark.Value, mark.Type, dialect)}";
            (mark.IsDate ? dateParts : incParts).Add(predicate);
        }

        var incExp = incParts.Count > 0 ? " AND " + string.Join(" AND ", incParts) : string.Empty;
        var dateExp = dateParts.Count > 0 ? " AND " + string.Join(" AND ", dateParts) : string.Empty;
        return (incExp, dateExp);
    }

    private static bool SourceMinIsLess(IReadOnlyList<Watermark> mins, IReadOnlyList<Watermark> maxes)
    {
        var maxByName = maxes.ToDictionary(m => m.Column, StringComparer.OrdinalIgnoreCase);
        foreach (var min in mins)
        {
            if (min.Value is null || !maxByName.TryGetValue(min.Column, out var max) || max.Value is null)
            {
                continue;
            }

            if (CompareScalar(min.Value, max.Value) < 0)
            {
                return true;
            }
        }

        return false;
    }

    // The source MIN and target MAX values come from two different tables, so their CLR types can differ even
    // for the same logical column (for example after the target widened int to bigint). Coerce to a common
    // type per family rather than calling Comparer<object>.Default, which throws on mismatched types.
    private static int CompareScalar(object a, object b)
    {
        if (a is byte[] left && b is byte[] right)
        {
            var length = Math.Min(left.Length, right.Length);
            for (var i = 0; i < length; i++)
            {
                var diff = left[i].CompareTo(right[i]);
                if (diff != 0)
                {
                    return diff;
                }
            }

            return left.Length.CompareTo(right.Length);
        }

        if (IsNumeric(a) && IsNumeric(b))
        {
            try
            {
                return Convert.ToDecimal(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(b, CultureInfo.InvariantCulture));
            }
            catch (OverflowException)
            {
                return Convert.ToDouble(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(b, CultureInfo.InvariantCulture));
            }
        }

        if (a is DateTimeOffset offsetA && b is DateTimeOffset offsetB)
        {
            return offsetA.CompareTo(offsetB);
        }

        if (a is DateTime || b is DateTime)
        {
            return Convert.ToDateTime(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDateTime(b, CultureInfo.InvariantCulture));
        }

        if (a.GetType() == b.GetType() && a is IComparable comparable)
        {
            return comparable.CompareTo(b);
        }

        return string.Compare(a.ToString(), b.ToString(), StringComparison.Ordinal);
    }

    private static bool IsNumeric(object value)
        => value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

    /// <summary>The amount to subtract from a non-date watermark's aggregate, or null when no shift applies:
    /// the flow declared no lookback, or the column is not one arithmetic can be done on. A string, binary or
    /// rowversion high-water column is left alone rather than being fed to a subtraction the source would
    /// reject; approximate types are excluded too, since shifting a float watermark is not a row count.</summary>
    private static int? NumericLookback(IngestionFlow flow, Watermark mark)
    {
        var lookback = flow.Incremental.Lookback;
        if (lookback <= 0 || mark.IsDate)
        {
            return null;
        }

        return mark.Type.BaseType.ToLowerInvariant() switch
        {
            "tinyint" or "smallint" or "int" or "bigint" or "decimal" or "numeric" or "money" or "smallmoney" => lookback,
            _ => null,
        };
    }

    /// <summary>Formats a backfill window bound (always a UTC <see cref="DateTime"/>) as a temporal literal for
    /// the date column's declared type, through the source dialect. Kept separate from
    /// <see cref="FormatLiteral"/>, which formats an introspected watermark value by its runtime CLR type.</summary>
    private static string BackfillLiteral(DateTime bound, string baseType, ISourceSqlDialect dialect)
    {
        var utc = DateTime.SpecifyKind(bound, DateTimeKind.Utc);
        var lower = baseType.ToLowerInvariant();
        var rendered = lower switch
        {
            "date" => utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "datetimeoffset" => utc.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture),
            _ => utc.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        };
        return dialect.FormatTemporalLiteral(lower, rendered);
    }

    private static string FormatLiteral(object value, SqlDataType type, ISourceSqlDialect dialect)
    {
        var baseType = type.BaseType.ToLowerInvariant();
        return baseType switch
        {
            "bit" => Convert.ToBoolean(value, CultureInfo.InvariantCulture) ? "1" : "0",
            "tinyint" or "smallint" or "int" or "bigint" => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            "decimal" or "numeric" or "money" or "smallmoney" => Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            "float" or "real" => Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture),
            "binary" or "varbinary" or "timestamp" or "rowversion" => dialect.FormatBinaryLiteral((byte[])value),
            "date" => dialect.FormatTemporalLiteral(baseType, ((DateTime)value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            "datetime" or "datetime2" or "smalldatetime" => dialect.FormatTemporalLiteral(baseType, Convert.ToDateTime(value, CultureInfo.InvariantCulture).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)),
            "datetimeoffset" => dialect.FormatTemporalLiteral(baseType, ((DateTimeOffset)value).ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)),
            "time" => dialect.FormatTemporalLiteral(baseType, ((TimeSpan)value).ToString("c", CultureInfo.InvariantCulture)),
            _ => "'" + (value.ToString() ?? string.Empty).Replace("'", "''", StringComparison.Ordinal) + "'",
        };
    }

    private static IReadOnlyList<Watermark> BuildMarkList(IngestionFlow flow, IReadOnlyDictionary<string, SqlDataType> typesByName)
    {
        var marks = new List<Watermark>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var column in flow.Incremental.Columns)
        {
            if (string.IsNullOrWhiteSpace(column) || !seen.Add(column))
            {
                continue;
            }

            marks.Add(new Watermark { Column = column, IsDate = false, Type = TypeFor(typesByName, column) });
        }

        var dateColumn = flow.Incremental.DateColumn;
        if (!string.IsNullOrWhiteSpace(dateColumn) && seen.Add(dateColumn))
        {
            marks.Add(new Watermark { Column = dateColumn, IsDate = true, Type = TypeFor(typesByName, dateColumn) });
        }

        return marks;
    }

    private static SqlDataType TypeFor(IReadOnlyDictionary<string, SqlDataType> typesByName, string column)
        => typesByName.TryGetValue(column, out var type)
            ? type
            : throw new SqlFlowException($"Incremental column '{column}' is not among the source columns.");

    private static string Clause(string? clause)
        => string.IsNullOrWhiteSpace(clause) ? string.Empty : " " + clause.Trim();

    private static ThreePartName ToName(RelationalObject relationalObject)
        => new() { Database = null, Schema = relationalObject.Schema, Name = relationalObject.Name };

    private static string SchemaQualified(RelationalObject relationalObject)
        => $"[{Escape(relationalObject.Schema)}].[{Escape(relationalObject.Name)}]";

    private static string Escape(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);

    private sealed record Watermark
    {
        public required string Column { get; init; }

        public required bool IsDate { get; init; }

        public required SqlDataType Type { get; init; }

        public object? Value { get; init; }
    }
}
