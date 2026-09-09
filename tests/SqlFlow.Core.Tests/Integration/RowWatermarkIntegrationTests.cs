using DuckDB.NET.Data;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using SqlFlow.Core.Model;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// End-to-end row-level (watermark-column) incremental against the physical sink, the path used for
/// Databricks/Parquet/Delta bulk loads. Proves a re-run over a grown dataset ingests only rows past the target's
/// MAX(watermarkColumn): for the typed Parquet reader (row-group pruning + engine filter), for DuckDB (predicate
/// pushdown), for a date/datetime watermark with an overlap window, and for the forced full-load escape hatch.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RowWatermarkIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_rowwm_" + Guid.NewGuid().ToString("N"));

    public RowWatermarkIntegrationTests() => Directory.CreateDirectory(_dir);

    private static readonly Lazy<bool> DuckDbAvailable = new(() =>
    {
        try
        {
            using var connection = new DuckDBConnection("DataSource=:memory:");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            command.ExecuteScalar();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    });

    /// <summary>Writes an int "Id" column over the given row groups (each an int[]), to exercise pruning.</summary>
    private async Task<string> WriteIdParquetAsync(string name, params int[][] rowGroups)
    {
        var path = Path.Combine(_dir, name);
        var field = new DataField<int>("Id");
        var schema = new ParquetSchema(field);

        await using var stream = File.Create(path);
        using var writer = await ParquetWriter.CreateAsync(schema, stream);
        foreach (var group in rowGroups)
        {
            using var rg = writer.CreateRowGroup();
            await rg.WriteColumnAsync(new DataColumn(field, group));
        }

        return path;
    }

    /// <summary>Writes "Id" (int) and "EventTime" (datetime) in a single row group.</summary>
    private async Task<string> WriteDatedParquetAsync(string name, (int Id, DateTime EventTime)[] rows)
    {
        var path = Path.Combine(_dir, name);
        var idField = new DataField<int>("Id");
        var timeField = new DataField<DateTime>("EventTime");
        var schema = new ParquetSchema(idField, timeField);

        await using var stream = File.Create(path);
        using var writer = await ParquetWriter.CreateAsync(schema, stream);
        using var rg = writer.CreateRowGroup();
        await rg.WriteColumnAsync(new DataColumn(idField, rows.Select(r => r.Id).ToArray()));
        await rg.WriteColumnAsync(new DataColumn(timeField, rows.Select(r => r.EventTime).ToArray()));
        return path;
    }

    private static FlowDefinition Flow(string cs, SourceSpec source, string table, IncrementalSpec incremental)
        => new()
        {
            Name = table,
            Source = source,
            Target = new TargetSpec { Connection = cs, Schema = IntegrationDb.Schema, Table = table },
            Schema = new SchemaPolicy { Evolve = SchemaEvolution.Widen },
            Load = new LoadPolicy(),
            Incremental = incremental,
        };

    private static SourceSpec Parquet(string path) => new() { Type = "parquet", Location = path, Options = new Dictionary<string, string?>() };

    private static SourceSpec DuckDb(string path) => new() { Type = "duckdb", Location = path, Options = new Dictionary<string, string?>() };

    [SkippableFact]
    public async Task Parquet_KeyBasedWatermark_LoadsOnlyNewRows()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_RowWmPrq_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        try
        {
            var inc = new IncrementalSpec { WatermarkColumn = "Id" };

            // First run into an empty target: no watermark yet, so the whole file loads.
            var first = await IntegrationDb.RealRunner().RunAsync(Flow(cs, Parquet(await WriteIdParquetAsync("v1.parquet", [1, 2, 3])), table, inc));
            Assert.Equal(FlowStatus.Success, first.Status);
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, table));

            // The dataset grew to 1..5 (two row groups). MAX(Id)=3, so only 4 and 5 are ingested; group [1,2,3]
            // is pruned and the engine filter drops nothing from the [4,5] group.
            var second = await IntegrationDb.RealRunner().RunAsync(Flow(cs, Parquet(await WriteIdParquetAsync("v2.parquet", [1, 2, 3], [4, 5])), table, inc));
            Assert.Equal(FlowStatus.Success, second.Status);
            Assert.Equal(2, second.RowsLoaded);
            Assert.Equal(5, await IntegrationDb.RowCountAsync(cs, table));

            // A re-run with no new rows is a clean no-op.
            var third = await IntegrationDb.RealRunner().RunAsync(Flow(cs, Parquet(await WriteIdParquetAsync("v3.parquet", [1, 2, 3, 4, 5])), table, inc));
            Assert.Equal(FlowStatus.Success, third.Status);
            Assert.Equal(0, third.RowsLoaded);
            Assert.Equal(5, await IntegrationDb.RowCountAsync(cs, table));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task DuckDb_KeyBasedWatermark_PushesDownAndLoadsOnlyNewRows()
    {
        var cs = IntegrationDb.Require();
        Skip.IfNot(DuckDbAvailable.Value, "libduckdb (DuckDB.NET native) could not be loaded.");

        var table = "IT_RowWmDuck_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        try
        {
            var inc = new IncrementalSpec { WatermarkColumn = "Id" };

            var first = await IntegrationDb.RealRunner().RunAsync(Flow(cs, DuckDb(await WriteIdParquetAsync("d1.parquet", [1, 2, 3])), table, inc));
            Assert.Equal(FlowStatus.Success, first.Status);
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, table));

            var second = await IntegrationDb.RealRunner().RunAsync(Flow(cs, DuckDb(await WriteIdParquetAsync("d2.parquet", [1, 2, 3, 4, 5])), table, inc));
            Assert.Equal(FlowStatus.Success, second.Status);
            Assert.Equal(2, second.RowsLoaded);
            Assert.Equal(5, await IntegrationDb.RowCountAsync(cs, table));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task Parquet_DateWatermark_OverlapWidensTheReadWindow()
    {
        var cs = IntegrationDb.Require();

        var dates = new[]
        {
            new DateTime(2026, 1, 1), new DateTime(2026, 1, 2), new DateTime(2026, 1, 3),
            new DateTime(2026, 1, 4), new DateTime(2026, 1, 5),
        };
        var seed = Enumerable.Range(0, 3).Select(i => (Id: i + 1, EventTime: dates[i])).ToArray();
        var grown = Enumerable.Range(0, 5).Select(i => (Id: i + 1, EventTime: dates[i])).ToArray();

        // Run the same grow against two independent targets, differing only by the overlap, and compare the rows
        // the watermark let through (RowsLoaded), isolating the overlap effect from the append/dedup behavior.
        var noOverlap = await SeedThenIncrementAsync(cs, "IT_RowWmDt0_" + Guid.NewGuid().ToString("N")[..6], seed, grown, overlap: 0);
        var twoDay = await SeedThenIncrementAsync(cs, "IT_RowWmDt2_" + Guid.NewGuid().ToString("N")[..6], seed, grown, overlap: 2);

        Assert.Equal(2, noOverlap);  // MAX=01-03, exact: 01-04, 01-05
        Assert.Equal(4, twoDay);     // MAX=01-03 minus 2 days = 01-01: 01-02, 01-03, 01-04, 01-05
    }

    private async Task<long> SeedThenIncrementAsync(string cs, string table, (int, DateTime)[] seed, (int, DateTime)[] grown, double overlap)
    {
        await IntegrationDb.DropTableAsync(cs, table);
        try
        {
            var inc = new IncrementalSpec { WatermarkColumn = "EventTime", WatermarkOverlap = overlap };

            var first = await IntegrationDb.RealRunner().RunAsync(Flow(cs, Parquet(await WriteDatedParquetAsync(table + "_a.parquet", seed)), table, inc));
            Assert.Equal(FlowStatus.Success, first.Status);

            var second = await IntegrationDb.RealRunner().RunAsync(Flow(cs, Parquet(await WriteDatedParquetAsync(table + "_b.parquet", grown)), table, inc));
            Assert.Equal(FlowStatus.Success, second.Status);
            return second.RowsLoaded;
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task FullLoadFlag_ReadsTheWholeSource()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_RowWmFull_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        try
        {
            var first = await IntegrationDb.RealRunner().RunAsync(
                Flow(cs, Parquet(await WriteIdParquetAsync("f1.parquet", [1, 2, 3])), table, new IncrementalSpec { WatermarkColumn = "Id" }));
            Assert.Equal(FlowStatus.Success, first.Status);

            // FullLoad ignores the watermark and re-reads everything (the whole file passes through).
            var full = await IntegrationDb.RealRunner().RunAsync(
                Flow(cs, Parquet(await WriteIdParquetAsync("f2.parquet", [1, 2, 3, 4, 5])), table, new IncrementalSpec { WatermarkColumn = "Id", FullLoad = true }));
            Assert.Equal(FlowStatus.Success, full.Status);
            Assert.Equal(5, full.RowsLoaded);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
