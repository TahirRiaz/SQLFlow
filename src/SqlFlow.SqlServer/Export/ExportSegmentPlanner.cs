using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Core.Export;
using SqlFlow.Core.Ingestion;
using SqlFlow.SqlServer.Ingestion;

namespace SqlFlow.SqlServer.Export;

/// <summary>One export chunk: the source SELECT, the file name (no extension), and its subfolder.</summary>
public sealed record ExportSegment(string Sql, string FileName, string SubFolder);

/// <summary>The value domain of a full-export chunk key (named like <see cref="SqlFlow.Core.Model.WatermarkKind"/>).</summary>
public enum FullExportKeyKind
{
    Whole,
    Fixed,
    Floating,
    DateTime,
    DateTimeOffset,
}

/// <summary>
/// The probed chunk key for a parallel full-table export: the key column, its value domain, and the observed
/// MIN/MAX. The runner probes it when a full export asks for more than one thread; the planner splits
/// [Min, Max] into contiguous per-thread ranges.
/// </summary>
public sealed record FullExportKeyRange
{
    public required string Column { get; init; }

    public required FullExportKeyKind Kind { get; init; }

    public required object Min { get; init; }

    public required object Max { get; init; }

    /// <summary>Maps a reader column's CLR type to its chunkable domain; null means the column cannot key
    /// a parallel full export.</summary>
    public static FullExportKeyKind? KindOf(Type clrType)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        if (clrType == typeof(byte) || clrType == typeof(short) || clrType == typeof(int) || clrType == typeof(long))
        {
            return FullExportKeyKind.Whole;
        }

        if (clrType == typeof(decimal))
        {
            return FullExportKeyKind.Fixed;
        }

        if (clrType == typeof(float) || clrType == typeof(double))
        {
            return FullExportKeyKind.Floating;
        }

        if (clrType == typeof(DateTime))
        {
            return FullExportKeyKind.DateTime;
        }

        if (clrType == typeof(DateTimeOffset))
        {
            return FullExportKeyKind.DateTimeOffset;
        }

        return null;
    }
}

/// <summary>
/// Plans the per-chunk source SELECTs and file names for an export, porting CommonDB.GetSrcSelectBatched and the
/// ExpSegment file-name composition. Chunks by day/month/key (reusing <see cref="ChunkRanges"/>, shared with
/// InitLoad); 'F' emits one segment per thread when a chunk key range was probed (NoOfThreads &gt; 1) and one
/// full-table segment otherwise. Date intervals are half-open and key intervals inclusive, with a trailing
/// NULL-rows segment for D/M/K and for a chunked 'F'. Applies the static SrcFilter (legacy dropped it).
/// Pure and deterministic given the bounds and the run timestamp.
/// </summary>
public static class ExportSegmentPlanner
{
    public static IReadOnlyList<ExportSegment> Plan(ExportFlow flow, IReadOnlyList<string> columns, int keyMax, DateTime runTimestamp, FullExportKeyRange? fullKeyRange = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(columns);

        var by = flow.ExportBy.Trim().ToUpperInvariant();
        var prefix = string.IsNullOrWhiteSpace(flow.TrgFileName) ? flow.Source.Name : flow.TrgFileName!;
        var timestamp = flow.AddTimeStampToFileName ? "_" + runTimestamp.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) : string.Empty;
        var columnList = string.Join(", ", columns.Select(c => $"[{Escape(c)}]"));

        // The full three-part name, as legacy GetSrcSelectBatched read it: the declared database is honored
        // even when the connection's default catalog differs (the source is always SQL Server, so the
        // cross-database form is always valid).
        var from = flow.Source.QualifiedName;
        var hint = TableHint(flow.SrcWithHint);
        var filter = string.IsNullOrWhiteSpace(flow.SrcFilter) ? string.Empty : " " + flow.SrcFilter.Trim();

        string Select(string batch) => $"SELECT {columnList} FROM {from}{hint} WHERE 1=1{filter}{batch}";

        var segments = new List<ExportSegment>();
        switch (by)
        {
            case "D":
            case "M":
            {
                if (string.IsNullOrWhiteSpace(flow.DateColumn))
                {
                    throw new SqlFlowException($"Export flow {flow.FlowId} uses ExportBy '{by}' but has no DateColumn.");
                }

                var dateColumn = Escape(flow.DateColumn);
                var from2 = flow.FromDate ?? DateOnly.FromDateTime(DateTime.Today.AddYears(-3));
                var to2 = flow.ToDate ?? DateOnly.FromDateTime(DateTime.Today);
                var ranges = by == "M" ? ChunkRanges.ByMonth(from2, to2, flow.ExportSize) : ChunkRanges.ByDay(from2, to2, flow.ExportSize);
                foreach (var (start, end) in ranges)
                {
                    var batch = $" AND ([{dateColumn}] >= '{Date(start)}' AND [{dateColumn}] < '{Date(end.AddDays(1))}')";
                    var postfix = start == end ? Date(start) : $"{Date(start)}-{Date(end)}";
                    segments.Add(new ExportSegment(Select(batch), $"{prefix}{timestamp}_{postfix}", SubFolder(flow, start)));
                }

                segments.Add(new ExportSegment(Select($" AND ([{dateColumn}] IS NULL)"), $"{prefix}{timestamp}_NullRows", string.Empty));
                break;
            }

            case "K":
            {
                if (string.IsNullOrWhiteSpace(flow.IncrementalColumn))
                {
                    throw new SqlFlowException($"Export flow {flow.FlowId} uses ExportBy 'K' but has no IncrementalColumn.");
                }

                var keyColumn = Escape(flow.IncrementalColumn);
                var width = Math.Max(1, keyMax).ToString(CultureInfo.InvariantCulture).Length;
                foreach (var (lo, hi) in ChunkRanges.ByKey(0, keyMax, flow.ExportSize))
                {
                    var batch = $" AND ([{keyColumn}] >= {lo} AND [{keyColumn}] <= {hi})";
                    var postfix = $"{lo.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0')}-{hi.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0')}";
                    segments.Add(new ExportSegment(Select(batch), $"{prefix}{timestamp}_{postfix}", string.Empty));
                }

                segments.Add(new ExportSegment(Select($" AND ([{keyColumn}] IS NULL)"), $"{prefix}{timestamp}_NullRows", string.Empty));
                break;
            }

            default:
            {
                // 'F' (or an unknown unit): full table. With a probed key range and NoOfThreads > 1 the
                // table splits into contiguous per-thread key ranges (plus the K-style NULL-rows segment)
                // so the runner's thread cap actually fans out; otherwise one full-table file.
                if (fullKeyRange is null || flow.NoOfThreads <= 1)
                {
                    segments.Add(new ExportSegment(Select(string.Empty), prefix + timestamp, string.Empty));
                    break;
                }

                var chunkColumn = Escape(fullKeyRange.Column);
                foreach (var chunk in FullExportChunks(fullKeyRange, flow.NoOfThreads))
                {
                    var comparison = chunk.InclusiveHi ? "<=" : "<";
                    var batch = $" AND ([{chunkColumn}] >= {chunk.Lo} AND [{chunkColumn}] {comparison} {chunk.Hi})";
                    segments.Add(new ExportSegment(Select(batch), $"{prefix}{timestamp}_{chunk.Postfix}", string.Empty));
                }

                segments.Add(new ExportSegment(Select($" AND ([{chunkColumn}] IS NULL)"), $"{prefix}{timestamp}_NullRows", string.Empty));
                break;
            }
        }

        return segments;
    }

    // One planned full-export slice: SQL-literal bounds, whether the upper bound is inclusive (only the last
    // slice, closed at MAX), and the file-name postfix.
    private sealed record FullExportChunk(string Lo, string Hi, bool InclusiveHi, string Postfix);

    private static IEnumerable<FullExportChunk> FullExportChunks(FullExportKeyRange range, int threads) => range.Kind switch
    {
        FullExportKeyKind.Whole => WholeChunks(
            Convert.ToInt64(range.Min, CultureInfo.InvariantCulture), Convert.ToInt64(range.Max, CultureInfo.InvariantCulture), threads),
        FullExportKeyKind.Fixed => FixedChunks(
            Convert.ToDecimal(range.Min, CultureInfo.InvariantCulture), Convert.ToDecimal(range.Max, CultureInfo.InvariantCulture), threads),
        FullExportKeyKind.Floating => FloatingChunks(
            Convert.ToDouble(range.Min, CultureInfo.InvariantCulture), Convert.ToDouble(range.Max, CultureInfo.InvariantCulture), threads),
        FullExportKeyKind.DateTime => DateTimeChunks((DateTime)range.Min, (DateTime)range.Max, threads),
        FullExportKeyKind.DateTimeOffset => DateTimeOffsetChunks((DateTimeOffset)range.Min, (DateTimeOffset)range.Max, threads),
        _ => throw new SqlFlowException($"Unsupported full-export chunk key kind '{range.Kind}'."),
    };

    // Inclusive [lo, hi] buckets in K-mode style: contiguous, the last closed at max. decimal arithmetic
    // is exact over the whole Int64 range, so the boundary math cannot overflow.
    private static IEnumerable<FullExportChunk> WholeChunks(long min, long max, int threads)
    {
        var width = Math.Max(
            min.ToString(CultureInfo.InvariantCulture).Length,
            max.ToString(CultureInfo.InvariantCulture).Length);

        // Zero padding keeps K-style sortable names; a negative bound renders unpadded ('-' cannot pad).
        string Pad(long value)
        {
            var text = value.ToString(CultureInfo.InvariantCulture);
            return value < 0 ? text : text.PadLeft(width, '0');
        }

        var span = (decimal)max - min;
        var lo = min;
        for (var i = 1; i <= threads && lo <= max; i++)
        {
            var hi = i == threads ? max : Math.Min(max, (long)((decimal)min + (span * i) / threads) - 1);
            if (hi < lo)
            {
                continue; // A rounding-collapsed slice; the next boundary covers it.
            }

            yield return new FullExportChunk(
                lo.ToString(CultureInfo.InvariantCulture), hi.ToString(CultureInfo.InvariantCulture),
                InclusiveHi: true, $"{Pad(lo)}-{Pad(hi)}");
            if (hi == max)
            {
                yield break; // lo would overflow past long.MaxValue.
            }

            lo = hi + 1;
        }
    }

    // Half-open [lo, hi) slices with the last closed at max: fractional keys cannot use inclusive hi-1
    // buckets. Boundaries divide before multiplying so the intermediate products stay inside decimal range
    // even for extreme decimal(38) keys.
    private static IEnumerable<FullExportChunk> FixedChunks(decimal min, decimal max, int threads)
    {
        string Lit(decimal value) => value.ToString(CultureInfo.InvariantCulture);

        var lo = min;
        for (var i = 1; i < threads; i++)
        {
            var hi = min - i * (min / threads) + i * (max / threads);
            if (hi <= lo)
            {
                continue;
            }

            if (hi >= max)
            {
                break;
            }

            yield return new FullExportChunk(Lit(lo), Lit(hi), InclusiveHi: false, $"{Lit(lo)}-{Lit(hi)}");
            lo = hi;
        }

        yield return new FullExportChunk(Lit(lo), Lit(max), InclusiveHi: true, $"{Lit(lo)}-{Lit(max)}");
    }

    // As FixedChunks, in the double domain. SQL Server cannot store NaN or infinity, so the probed
    // MIN/MAX are always finite.
    private static IEnumerable<FullExportChunk> FloatingChunks(double min, double max, int threads)
    {
        string Lit(double value) => value.ToString("R", CultureInfo.InvariantCulture);

        var step = (max - min) / threads;
        var lo = min;
        for (var i = 1; i < threads; i++)
        {
            var hi = min + step * i;
            if (hi <= lo)
            {
                continue;
            }

            if (hi >= max)
            {
                break;
            }

            yield return new FullExportChunk(Lit(lo), Lit(hi), InclusiveHi: false, $"{Lit(lo)}-{Lit(hi)}");
            lo = hi;
        }

        yield return new FullExportChunk(Lit(lo), Lit(max), InclusiveHi: true, $"{Lit(lo)}-{Lit(max)}");
    }

    // Half-open [lo, hi) slices with the last closed at max. Boundaries snap down to whole seconds so the
    // second-precision file-name postfix stays unique per slice.
    private static IEnumerable<FullExportChunk> DateTimeChunks(DateTime min, DateTime max, int threads)
    {
        string Lit(DateTime value) => $"'{value.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture)}'";
        string Name(DateTime value) => value.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

        var span = (decimal)max.Ticks - min.Ticks;
        var lo = min;
        for (var i = 1; i < threads; i++)
        {
            var ticks = (long)((decimal)min.Ticks + (span * i) / threads);
            var hi = new DateTime(ticks - (ticks % TimeSpan.TicksPerSecond), min.Kind);
            if (hi <= lo)
            {
                continue;
            }

            if (hi >= max)
            {
                break;
            }

            yield return new FullExportChunk(Lit(lo), Lit(hi), InclusiveHi: false, $"{Name(lo)}-{Name(hi)}");
            lo = hi;
        }

        yield return new FullExportChunk(Lit(lo), Lit(max), InclusiveHi: true, $"{Name(lo)}-{Name(max)}");
    }

    // As DateTimeChunks, normalized to UTC: datetimeoffset orders by the UTC instant, so the boundaries
    // are computed and emitted as +00:00 literals.
    private static IEnumerable<FullExportChunk> DateTimeOffsetChunks(DateTimeOffset min, DateTimeOffset max, int threads)
    {
        string Lit(DateTimeOffset value) => $"'{value.ToString("yyyy-MM-ddTHH:mm:ss.fffffff'+00:00'", CultureInfo.InvariantCulture)}'";
        string Name(DateTimeOffset value) => value.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

        var minUtc = min.ToUniversalTime();
        var maxUtc = max.ToUniversalTime();
        var span = (decimal)maxUtc.UtcTicks - minUtc.UtcTicks;
        var lo = minUtc;
        for (var i = 1; i < threads; i++)
        {
            var ticks = (long)((decimal)minUtc.UtcTicks + (span * i) / threads);
            var hi = new DateTimeOffset(ticks - (ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
            if (hi <= lo)
            {
                continue;
            }

            if (hi >= maxUtc)
            {
                break;
            }

            yield return new FullExportChunk(Lit(lo), Lit(hi), InclusiveHi: false, $"{Name(lo)}-{Name(hi)}");
            lo = hi;
        }

        yield return new FullExportChunk(Lit(lo), Lit(maxUtc), InclusiveHi: true, $"{Name(lo)}-{Name(maxUtc)}");
    }

    // Subfolders distribute D/M files by the chunk-start date: each YYYY/MM/DD token in the pattern is a folder
    // level (legacy PatternToSubfolderPath).
    private static string SubFolder(ExportFlow flow, DateOnly date)
    {
        if (string.IsNullOrWhiteSpace(flow.Subfolderpattern))
        {
            return string.Empty;
        }

        var pattern = flow.Subfolderpattern.ToUpperInvariant();
        var parts = new List<string>();
        if (pattern.Contains("YYYY", StringComparison.Ordinal))
        {
            parts.Add(date.Year.ToString("D4", CultureInfo.InvariantCulture));
        }

        if (pattern.Contains("MM", StringComparison.Ordinal))
        {
            parts.Add(date.Month.ToString("D2", CultureInfo.InvariantCulture));
        }

        if (pattern.Contains("DD", StringComparison.Ordinal))
        {
            parts.Add(date.Day.ToString("D2", CultureInfo.InvariantCulture));
        }

        return parts.Count == 0 ? string.Empty : string.Join("/", parts) + "/";
    }

    private static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // Renders the optional source table hint (legacy srcWithHint) as a single WITH (...) clause, accepting the
    // operator's value in any of the common spellings ("NOLOCK", "(NOLOCK)", "WITH (NOLOCK)", "NOLOCK, INDEX(..)")
    // and normalizing to exactly one WITH (...) so it is emitted right after the table reference. Blank means no
    // hint. The content is operator-supplied trusted config (raw-append, like SrcFilter). Shared with the runner
    // so the key-max probe reads the source under the same hint as the data SELECTs.
    internal static string TableHint(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var inner = raw.Trim();
        if (inner.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
        {
            inner = inner[4..].Trim();
        }

        // Strip exactly one wrapping paren pair when the whole value is wrapped ("(NOLOCK)"), but never a paren
        // that belongs to a hint argument ("NOLOCK, INDEX(IX)"): only unwrap when the leading '(' is the one the
        // trailing ')' closes.
        if (IsWrappedInParens(inner))
        {
            inner = inner[1..^1].Trim();
        }

        return inner.Length == 0 ? string.Empty : $" WITH ({inner})";
    }

    private static bool IsWrappedInParens(string value)
    {
        if (value.Length < 2 || value[0] != '(' || value[^1] != ')')
        {
            return false;
        }

        var depth = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '(')
            {
                depth++;
            }
            else if (value[i] == ')')
            {
                depth--;
                // The opening '(' closed before the end, so the value is not a single wrapped group.
                if (depth == 0 && i < value.Length - 1)
                {
                    return false;
                }
            }
        }

        return depth == 0;
    }

    private static string Escape(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);
}
