using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Ingestion;

namespace SqlFlow.SqlServer.Ingestion;

/// <summary>One chunk of an InitLoad backfill: the WHERE fragment and the full source SELECT for it.</summary>
public sealed record InitLoadSegment(string WhereClause, string Sql);

/// <summary>
/// Plans the chunked source reads for a one-time InitLoad backfill, porting the legacy
/// CommonDB.GetSrcSelectBatched plus Functions.BatchByMonth / BatchByDay / GetKeyRanges. It chunks by month
/// ('M'), day ('D'), or integer key ('K'); date intervals are half-open <c>[start, end+1day)</c> and key
/// intervals inclusive <c>[lo, hi]</c>; a trailing <c>IS NULL</c> segment is always appended so rows with a
/// NULL date/key are not dropped. The legacy metadata-SP defaults are applied here (V3 does not run that SP):
/// three years back, today, month chunks of size 1, key max 10,000,000. The date column is the flow's
/// incremental DateColumn (legacy has no separate InitLoad date column). Pure and deterministic given the
/// inputs; the only ambient value is "today", used solely when a bound is unset (exactly as the legacy SP).
/// </summary>
public static class InitLoadPlanner
{
    private const int DefaultKeyMaxValue = 10_000_000;

    public static IReadOnlyList<InitLoadSegment> Plan(IngestionFlow flow, RelationalObject source, IReadOnlyList<string> sourceColumns, ISourceSqlDialect? dialect = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceColumns);
        dialect ??= new SqlServerSourceDialect();

        var by = (flow.InitLoad.BatchBy ?? "M").Trim().ToUpperInvariant();
        var dateColumn = flow.Incremental.DateColumn;
        var keyColumn = flow.InitLoad.KeyColumn;

        // Trust-boundary validation: legacy would emit broken SQL here; V3 fails fast with a clear message.
        if ((by == "D" || by == "M") && string.IsNullOrWhiteSpace(dateColumn))
        {
            throw new SqlFlowException("InitLoad by date requires Incremental.DateColumn to be set.");
        }

        if (by == "K" && string.IsNullOrWhiteSpace(keyColumn))
        {
            throw new SqlFlowException("InitLoad by key requires InitLoad.KeyColumn to be set.");
        }

        var prefix = BuildPrefix(source, sourceColumns, flow.Source.Filter, dialect);
        var segments = new List<InitLoadSegment>();

        switch (by)
        {
            case "M":
            case "D":
            {
                var from = flow.InitLoad.FromDate ?? DateOnly.FromDateTime(DateTime.Today.AddYears(-3));
                var to = flow.InitLoad.ToDate ?? DateOnly.FromDateTime(DateTime.Today);
                var size = flow.InitLoad.BatchSize ?? 1;
                var quotedDate = dialect.QuoteIdentifier(dateColumn!);
                var ranges = by == "M" ? ChunkRanges.ByMonth(from, to, size) : ChunkRanges.ByDay(from, to, size);
                foreach (var (start, end) in ranges)
                {
                    var where =
                        $" AND ({quotedDate} >= '{Date(start)}' AND {quotedDate} < '{Date(end.AddDays(1))}')";
                    segments.Add(new InitLoadSegment(where, prefix + where));
                }

                var nullDate = $" AND ({quotedDate} IS NULL)";
                segments.Add(new InitLoadSegment(nullDate, prefix + nullDate));
                break;
            }

            case "K":
            {
                var size = flow.InitLoad.BatchSize ?? 1;
                var keyMax = flow.InitLoad.KeyMaxValue ?? DefaultKeyMaxValue;
                var quotedKey = dialect.QuoteIdentifier(keyColumn!);
                foreach (var (lo, hi) in ChunkRanges.ByKey(0, keyMax, size))
                {
                    var where = $" AND ({quotedKey} >= {lo} AND {quotedKey} <= {hi})";
                    segments.Add(new InitLoadSegment(where, prefix + where));
                }

                var nullKey = $" AND ({quotedKey} IS NULL)";
                segments.Add(new InitLoadSegment(nullKey, prefix + nullKey));
                break;
            }

            default:
                // Unknown unit: legacy returns an empty list and streams nothing (no throw).
                return [];
        }

        return segments;
    }

    // Calendar-aligned month chunks (snap to month-end), with the first/last chunk clamped to the window;
    private static string BuildPrefix(RelationalObject source, IReadOnlyList<string> columns, string? filter, ISourceSqlDialect dialect)
    {
        var columnList = string.Join(", ", columns.Select(dialect.QuoteIdentifier));
        var filterClause = string.IsNullOrWhiteSpace(filter) ? string.Empty : " " + filter.Trim();
        return $"SELECT {columnList} FROM {dialect.QualifyObject(source)} WHERE 1=1{filterClause}";
    }

    private static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
