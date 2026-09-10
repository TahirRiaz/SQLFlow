using Parquet;
using Parquet.Data;
using Parquet.Schema;
using SqlFlow.Core.Model;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// End-to-end Parquet ingestion against the physical sink, proving the columnar reader rides the same engine
/// path as CSV/XLS/JSON/XML: the file's own schema becomes columns, native types are rendered as faithful
/// strings and bulk-loaded with the provenance columns, and the schema evolves (additive union, null-fill)
/// across files. Each test drops its table up front.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ParquetIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_prqit_" + Guid.NewGuid().ToString("N"));

    public ParquetIntegrationTests() => Directory.CreateDirectory(_dir);

    private async Task<string> WriteAsync(string fileName, ParquetSchema schema, params Array[] columns)
    {
        var path = Path.Combine(_dir, fileName);
        var leaves = schema.GetDataFields();
        await using var stream = File.Create(path);
        using var writer = await ParquetWriter.CreateAsync(schema, stream);
        using var rg = writer.CreateRowGroup();
        for (var i = 0; i < leaves.Length; i++)
        {
            await rg.WriteColumnAsync(new DataColumn(leaves[i], columns[i]));
        }

        return path;
    }

    private static FlowDefinition Flow(string table, string connection, string location, Dictionary<string, string?>? options = null)
        => new()
        {
            Name = table,
            Source = new SourceSpec { Type = "parquet", Location = location, Options = options ?? new Dictionary<string, string?>() },
            Target = new TargetSpec { Connection = connection, Schema = IntegrationDb.Schema, Table = table },
            Schema = new SchemaPolicy { Evolve = SchemaEvolution.Widen },
            Load = new LoadPolicy { Mode = LoadMode.TruncateLoad },
        };

    private static Task<string?> ColumnTypeAsync(string cs, string table, string column)
        => IntegrationDb.ScalarAsync<string?>(cs,
            $"SELECT DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = '{table}' AND COLUMN_NAME = '{column}'");

    [SkippableFact]
    public async Task Parquet_MapsParquetTypesToSqlTypes_AndLoadsTypedValues()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_Prq_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        var path = await WriteAsync("orders.parquet",
            new ParquetSchema(new DataField<int>("id"), new DataField<string>("customer"), new DataField<double>("total")),
            new int[] { 1, 2 }, new string?[] { "Ann", "Bob" }, new double[] { 9.5, 3.0 });

        try
        {
            var result = await IntegrationDb.RealRunner().RunAsync(Flow(table, cs, path));

            Assert.Equal(FlowStatus.Success, result.Status);
            Assert.Equal(2, result.RowsLoaded);
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, table));

            // Parquet's native types become real SQL types - not a catch-all nvarchar.
            Assert.Equal("int", await ColumnTypeAsync(cs, table, "id"));
            Assert.Equal("float", await ColumnTypeAsync(cs, table, "total"));
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, table, "FileName_DW"));
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, table, "RowNumber_DW"));

            // The typed values load and are queryable as their real types.
            Assert.Equal("Bob", await IntegrationDb.ScalarAsync<string?>(cs,
                $"SELECT [customer] FROM [dbo].[{table}] WHERE [id] = 2"));
            Assert.Equal(9.5d, await IntegrationDb.ScalarAsync<double>(cs,
                $"SELECT [total] FROM [dbo].[{table}] WHERE [id] = 1"));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task Parquet_NestedListColumn_LandsAsJsonText()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_PrqNested_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        var schema = new ParquetSchema(new DataField<int>("id"), new ListField("tags", new DataField<int>("element")));
        var path = Path.Combine(_dir, "nested.parquet");
        var leaves = schema.GetDataFields();
        await using (var stream = File.Create(path))
        using (var writer = await ParquetWriter.CreateAsync(schema, stream))
        using (var rg = writer.CreateRowGroup())
        {
            await rg.WriteColumnAsync(new DataColumn(leaves[0], new int[] { 1 }));
            await rg.WriteColumnAsync(new DataColumn(leaves[1], new int[] { 7, 8, 9 }, repetitionLevels: new[] { 0, 1, 1 }));
        }

        try
        {
            var result = await IntegrationDb.RealRunner().RunAsync(Flow(table, cs, path));

            Assert.Equal(FlowStatus.Success, result.Status);
            Assert.Equal("int", await ColumnTypeAsync(cs, table, "id"));         // scalar stays typed
            Assert.Equal("nvarchar", await ColumnTypeAsync(cs, table, "tags"));  // list becomes JSON text
            Assert.Equal("[7,8,9]", await IntegrationDb.ScalarAsync<string?>(cs,
                $"SELECT [tags] FROM [dbo].[{table}] WHERE [id] = 1"));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task Parquet_SchemaEvolution_UnionsColumnsAcrossFiles()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_PrqEvolve_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        await WriteAsync("v1.parquet", new ParquetSchema(new DataField<int>("id"), new DataField<string>("name")),
            new int[] { 1 }, new string?[] { "Ann" });
        await WriteAsync("v2.parquet", new ParquetSchema(new DataField<int>("id"), new DataField<string>("email")),
            new int[] { 2 }, new string?[] { "b@x.io" });
        var options = new Dictionary<string, string?> { ["srcFile"] = "*.parquet" };

        try
        {
            var result = await IntegrationDb.RealRunner().RunAsync(Flow(table, cs, _dir, options));

            Assert.Equal(FlowStatus.Success, result.Status);
            Assert.Equal(2, result.RowsLoaded);
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, table, "name"));
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, table, "email"));
            Assert.Null(await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [email] FROM [dbo].[{table}] WHERE [id] = '1'"));
            Assert.Equal("b@x.io", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [email] FROM [dbo].[{table}] WHERE [id] = '2'"));
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
