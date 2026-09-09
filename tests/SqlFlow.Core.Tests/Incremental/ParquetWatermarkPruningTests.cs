using Parquet;
using Parquet.Data;
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
        using var writer = await ParquetWriter.CreateAsync(schema, stream);
        foreach (var group in new[] { new[] { 1, 2, 3 }, new[] { 4, 5, 6 }, new[] { 7, 8, 9 } })
        {
            using var rg = writer.CreateRowGroup();
            await rg.WriteColumnAsync(new DataColumn(field, group));
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

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
