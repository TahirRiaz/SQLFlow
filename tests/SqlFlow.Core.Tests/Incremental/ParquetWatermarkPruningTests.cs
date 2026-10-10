using DuckDB.NET.Data;
using Parquet;
using Parquet.Schema;
using SqlFlow.Core.Engine;
using SqlFlow.Core.Model;
using SqlFlow.Sources;
using Xunit;

namespace SqlFlow.Tests.Incremental;

/// <summary>
/// Row-level incremental for the pure-managed Parquet reader. Parquet carries per-row-group min/max statistics,
/// so the reader skips whole row groups at or below the watermark (the same efficiency DuckDB gets from
/// pushdown), reading only the groups that can contain new rows. Correctness is completed by the engine's
/// row-level filter, which drops any straggler rows inside a kept group; the final test layers the two to prove
/// the exact incremental result. No database: real temp .parquet files with three row groups.
/// </summary>
public sealed class ParquetWatermarkPruningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_prqinc_" + Guid.NewGuid().ToString("N"));
    private readonly ParquetSourceReader _reader = new(new LocalFileLifecycle(), [new LocalFileStore()]);

    public ParquetWatermarkPruningTests() => Directory.CreateDirectory(_dir);

    /// <summary>Writes one int column "Id" across three row groups: [1,2,3], [4,5,6], [7,8,9].</summary>
    private async Task<string> WriteThreeGroupsAsync()
    {
        var path = Path.Combine(_dir, "data.parquet");
        var field = new DataField<int>("Id");
        var schema = new ParquetSchema(field);

        await using var stream = File.Create(path);
        await using var writer = await ParquetWriter.CreateAsync(schema, stream);
        foreach (var group in new[] { new[] { 1, 2, 3 }, new[] { 4, 5, 6 }, new[] { 7, 8, 9 } })
        {
            using var rg = writer.CreateRowGroup();
            await rg.WriteColumnAsync(field, group);
        }

        return path;
    }

    private static SourceSpec Source(string path, string? watermarkValue)
    {
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (watermarkValue is not null)
        {
            options[WatermarkPredicate.ColumnOption] = "Id";
            options[WatermarkPredicate.ValueOption] = watermarkValue;
            options[WatermarkPredicate.KindOption] = WatermarkKind.Whole.ToString();
        }

        return new SourceSpec { Type = "parquet", Location = path, Options = options };
    }

    private async Task<List<int>> ReadIdsAsync(SourceSpec source, bool applyEngineFilter)
    {
        var columns = await _reader.GetColumnsAsync(source);
        var read = await _reader.OpenAsync(source, columns);
        System.Data.Common.DbDataReader data = read.Reader;
        if (applyEngineFilter && source.Options.TryGetValue(WatermarkPredicate.ValueOption, out var value))
        {
            data = new WatermarkFilteringDataReader(read.Reader, "Id", value!, WatermarkKind.Whole);
        }

        await using (data)
        {
            var ordinal = data.GetOrdinal("Id");
            var ids = new List<int>();
            while (await data.ReadAsync())
            {
                ids.Add(data.GetInt32(ordinal));
            }

            return ids;
        }
    }

    [Fact]
    public async Task PrunesRowGroupsAtOrBelowTheBound()
    {
        var path = await WriteThreeGroupsAsync();

        // Bound 5: group [1,2,3] (max 3) is pruned; groups [4,5,6] and [7,8,9] are read whole (stragglers 4,5
        // survive the reader and are removed later by the engine filter).
        var ids = await ReadIdsAsync(Source(path, "5"), applyEngineFilter: false);

        Assert.Equal([4, 5, 6, 7, 8, 9], ids);
    }

    [Fact]
    public async Task NoWatermark_ReadsEveryGroup()
    {
        var path = await WriteThreeGroupsAsync();
        var ids = await ReadIdsAsync(Source(path, watermarkValue: null), applyEngineFilter: false);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9], ids);
    }

    [Fact]
    public async Task BoundAboveEverything_PrunesAllGroups()
    {
        var path = await WriteThreeGroupsAsync();
        var ids = await ReadIdsAsync(Source(path, "100"), applyEngineFilter: false);
        Assert.Empty(ids);
    }

    [Fact]
    public async Task PruningPlusEngineFilter_YieldsExactIncrementalResult()
    {
        var path = await WriteThreeGroupsAsync();

        // Reader prunes group 1 and reads groups 2 and 3; the engine filter then drops stragglers 4 and 5.
        var ids = await ReadIdsAsync(Source(path, "5"), applyEngineFilter: true);

        Assert.Equal([6, 7, 8, 9], ids);
    }

    /// <summary>Writes one leaf across row groups, one array per group.</summary>
    private async Task<string> WriteGroupsAsync(string name, DataField field, params Array[] groups)
    {
        var path = Path.Combine(_dir, name);
        await using var stream = File.Create(path);
        await using var writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), stream);
        foreach (var group in groups)
        {
            using var rg = writer.CreateRowGroup();
            await rg.WriteColumnAsync(field, group);
        }

        return path;
    }

    /// <summary>Every cell of one column the reader yields under a watermark bound (pruning only, no engine filter).</summary>
    private async Task<List<object?>> ReadPrunedAsync(string path, string column, string bound, WatermarkKind kind)
    {
        var source = new SourceSpec
        {
            Type = "parquet",
            Location = path,
            Options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [WatermarkPredicate.ColumnOption] = column,
                [WatermarkPredicate.ValueOption] = bound,
                [WatermarkPredicate.KindOption] = kind.ToString(),
            },
        };

        var columns = await _reader.GetColumnsAsync(source);
        var read = await _reader.OpenAsync(source, columns);
        await using var data = read.Reader;
        var ordinal = data.GetOrdinal(column);
        var cells = new List<object?>();
        while (await data.ReadAsync())
        {
            cells.Add(data.IsDBNull(ordinal) ? null : data.GetValue(ordinal));
        }

        return cells;
    }

    private static DateTime Day(int day) => new(2026, 1, day, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task TimestampWatermark_PrunesByTheDecodedInstant()
    {
        // The maximum of a TIMESTAMP column is stored as an epoch offset; it is decoded to the DateTime the cells
        // carry. (Compared raw, it used to fail the whole read with an invalid cast.)
        var field = new DateTimeDataField("At", DateTimeFormat.Timestamp, isAdjustedToUTC: true, unit: DateTimeTimeUnit.Millis);
        var path = await WriteGroupsAsync("ts.parquet", field,
            new DateTime?[] { Day(1), Day(2) }, new DateTime?[] { Day(3), Day(4) }, new DateTime?[] { Day(5), Day(6) });

        var cells = await ReadPrunedAsync(path, "At", "2026-01-03T00:00:00", WatermarkKind.DateTime);

        Assert.Equal([Day(3), Day(4), Day(5), Day(6)], cells.Cast<DateTime>().Select(d => d.ToUniversalTime()));
    }

    [Fact]
    public async Task MicrosecondTimestampWrittenByParquetNet_IsNotPruned()
    {
        // Parquet.Net stores TIMESTAMP statistics in milliseconds whatever the declared unit, so its microsecond
        // maximum here reads as a day in January 1970 and would skip every group. Such statistics are not trusted.
        var field = new DateTimeDataField("At", DateTimeFormat.Timestamp, isAdjustedToUTC: true, unit: DateTimeTimeUnit.Micros);
        var path = await WriteGroupsAsync("ts_us.parquet", field, new DateTime?[] { Day(1), Day(2) }, new DateTime?[] { Day(3), Day(4) });

        var cells = await ReadPrunedAsync(path, "At", "2026-01-02T00:00:00", WatermarkKind.DateTime);

        Assert.Equal([Day(1), Day(2), Day(3), Day(4)], cells.Cast<DateTime>().Select(d => d.ToUniversalTime()));
    }

    [Fact]
    public async Task MicrosecondTimestampWithCorrectStatistics_PrunesExactlyTheOldGroups()
    {
        // DuckDB writes spec-correct microsecond statistics: three row groups of 2048 one-second-apart rows. The bound
        // is the first group's last instant, so that group (and only it) is skipped, and every later row survives.
        var path = Path.Combine(_dir, "duckdb_ts.parquet");
        using (var connection = new DuckDBConnection("DataSource=:memory:"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SET threads = 1; "
                + "COPY (SELECT TIMESTAMP '2026-01-01 00:00:00' + to_seconds(i) AS At FROM range(6144) t(i) ORDER BY i) "
                + $"TO '{path.Replace("'", "''", StringComparison.Ordinal)}' (FORMAT PARQUET, ROW_GROUP_SIZE 2048);";
            command.ExecuteNonQuery();
        }

        var cells = await ReadPrunedAsync(path, "At", "2026-01-01T00:34:07", WatermarkKind.DateTime);

        Assert.Equal(4096, cells.Count);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 34, 8), (DateTime)cells[0]!, TimeSpan.Zero);
        Assert.Equal(new DateTime(2026, 1, 1, 1, 42, 23), (DateTime)cells[^1]!, TimeSpan.Zero);
    }

    [Fact]
    public async Task DateWatermark_PrunesByTheDecodedDay()
    {
        var field = new DataField<DateOnly>("Day");
        var path = await WriteGroupsAsync("date.parquet", field,
            new DateOnly[] { new(2026, 1, 1), new(2026, 1, 2) }, new DateOnly[] { new(2026, 1, 3), new(2026, 1, 4) });

        var cells = await ReadPrunedAsync(path, "Day", "2026-01-02", WatermarkKind.DateTime);

        Assert.Equal([Day(3), Day(4)], cells.Cast<DateTime>());
    }

    [Fact]
    public async Task DecimalWatermark_PrunesByTheDecodedAmount()
    {
        // A decimal maximum is the big-endian unscaled integer; it is decoded at the column's scale. (Compared raw, it
        // used to fail the whole read with an invalid cast.)
        var field = new DecimalDataField("Amount", 10, 2);
        var path = await WriteGroupsAsync("dec.parquet", field,
            new decimal?[] { -5.25m, 1.10m }, new decimal?[] { 12.50m, 2.00m }, new decimal?[] { 13.75m, 99.99m });

        var cells = await ReadPrunedAsync(path, "Amount", "12.50", WatermarkKind.Fixed);

        Assert.Equal([13.75m, 99.99m], cells.Cast<decimal>());
    }

    [Fact]
    public async Task TextWatermark_PrunesByTheDecodedString()
    {
        var field = new DataField<string>("Code");
        var path = await WriteGroupsAsync("text.parquet", field, new[] { "a", "b" }, new[] { "c", "d" });

        var cells = await ReadPrunedAsync(path, "Code", "b", WatermarkKind.Text);

        Assert.Equal(["c", "d"], cells.Cast<string>());
    }

    [Fact]
    public async Task UnsignedColumn_IsNeverPruned_BecauseItsStatisticIsStoredSigned()
    {
        // 3,000,000,000 is stored as a negative INT32, so a raw maximum would look "at or below" any bound and the
        // group would be skipped, losing its rows. Unsigned columns are read in full and left to the row filter.
        var field = new DataField<uint>("Seq");
        var path = await WriteGroupsAsync("uint.parquet", field, new uint[] { 1, 2, 3 }, new uint[] { 3_000_000_000, 3_000_000_001 });

        var cells = await ReadPrunedAsync(path, "Seq", "5", WatermarkKind.Whole);

        Assert.Equal([1L, 2L, 3L, 3_000_000_000L, 3_000_000_001L], cells.Cast<long>());
    }

    [Fact]
    public async Task TimeColumn_IsNeverPruned_BecauseItsCellsAreText()
    {
        // TIME cells are duration text while the statistic is an integer of milliseconds: the two do not order alike,
        // so no group is skipped on the statistic.
        var field = new TimeDataField("At", TimeUnitPrecision.Millis);
        var path = await WriteGroupsAsync("time.parquet", field, new int?[] { 3_600_000, 7_200_000 }, new int?[] { 10_800_000 });

        var cells = await ReadPrunedAsync(path, "At", "99", WatermarkKind.Text);

        Assert.Equal(["01:00:00", "02:00:00", "03:00:00"], cells.Cast<string>());
    }

    [Fact]
    public async Task TextMaximumWithSupplementaryCharacters_IsNotPruned()
    {
        // Parquet orders text by code point, the watermark by UTF-16 unit: U+1F600 is the group's Parquet maximum but
        // sorts below U+F000 in UTF-16, so pruning on it would drop the U+F000 row that is past the U+E000 bound.
        var field = new DataField<string>("Code");
        var path = await WriteGroupsAsync("surrogate.parquet", field, new[] { "", "\U0001F600" });

        var cells = await ReadPrunedAsync(path, "Code", "", WatermarkKind.Text);

        Assert.Equal(["", "\U0001F600"], cells.Cast<string>());
    }

    [Fact]
    public async Task IncomparableWatermarkKind_ReadsTheGroupsInsteadOfFailing()
    {
        // A DateTime bound over an integer column cannot be compared with the statistic: pruning backs off and the
        // engine's row-level filter is left to decide (and report) row by row.
        var path = await WriteThreeGroupsAsync();

        var cells = await ReadPrunedAsync(path, "Id", "2026-01-01T00:00:00", WatermarkKind.DateTime);

        Assert.Equal(9, cells.Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
