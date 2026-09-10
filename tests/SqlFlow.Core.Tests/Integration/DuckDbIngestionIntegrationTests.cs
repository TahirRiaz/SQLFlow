using DuckDB.NET.Data;
using SqlFlow.Core.Model;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// End-to-end DuckDB source into the real SQL Server sink: a Parquet fixture is read by the DuckDB reader and
/// bulk-loaded through the file-flow engine (the TABLOCK + max-packet-size fast path), proving the DuckDB reader
/// rides the same pipeline as every other source. Also exercises predicate/column pushdown. Skips when the sink
/// is unreachable or libduckdb cannot load.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DuckDbIngestionIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_duckdb_it_" + Guid.NewGuid().ToString("N"));

    public DuckDbIngestionIntegrationTests() => Directory.CreateDirectory(_dir);

    private string WriteParquet(string copySelect)
    {
        var path = Path.Combine(_dir, "data.parquet").Replace('\\', '/');
        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"COPY ({copySelect}) TO '{path}' (FORMAT PARQUET);";
        command.ExecuteNonQuery();
        return path;
    }

    private static bool DuckDbAvailable()
    {
        try
        {
            using var c = new DuckDBConnection("DataSource=:memory:");
            c.Open();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [SkippableFact]
    public async Task DuckDbParquet_LoadsIntoSink_ViaFileFlow()
    {
        var cs = IntegrationDb.Require();
        Skip.IfNot(DuckDbAvailable(), "libduckdb (DuckDB.NET native) could not be loaded.");

        var table = "IT_DuckDb_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);
        var path = WriteParquet(
            "SELECT * FROM (VALUES (1,'Ann',CAST(10.50 AS DECIMAL(8,2))),(2,'Bob',CAST(20.00 AS DECIMAL(8,2))),(3,'Cy',CAST(5.25 AS DECIMAL(8,2)))) t(id,name,amt)");

        var flow = new FlowDefinition
        {
            Name = table,
            Source = new SourceSpec { Type = "duckdb", Location = path, Options = new Dictionary<string, string?>() },
            Target = new TargetSpec { Connection = cs, Schema = IntegrationDb.Schema, Table = table },
            Schema = new SchemaPolicy { Evolve = SchemaEvolution.Widen },
            Load = new LoadPolicy(),
        };

        try
        {
            var result = await IntegrationDb.RealRunner().RunAsync(flow);

            Assert.Equal(FlowStatus.Success, result.Status);
            Assert.Equal(3, result.RowsLoaded);
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, table));
            // The typed Parquet schema flows through DuckDB to faithful SQL Server types (not strings).
            Assert.Equal("int", await IntegrationDb.ColumnTypeAsync(cs, table, "id"));
            Assert.Equal("Bob", await IntegrationDb.ScalarAsync<string?>(cs,
                $"SELECT [name] FROM [dbo].[{table}] WHERE [id] = '2'"));
            Assert.Equal(5.25m, await IntegrationDb.ScalarAsync<decimal?>(cs,
                $"SELECT [amt] FROM [dbo].[{table}] WHERE [id] = '3'"));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task DuckDbPushdown_LoadsOnlyTheProjectedFilteredRows()
    {
        var cs = IntegrationDb.Require();
        Skip.IfNot(DuckDbAvailable(), "libduckdb (DuckDB.NET native) could not be loaded.");

        var table = "IT_DuckDbPush_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);
        var path = WriteParquet("SELECT * FROM (VALUES (1,'Ann',100),(2,'Bob',5),(3,'Cy',999)) t(id,name,score)");

        var flow = new FlowDefinition
        {
            Name = table,
            Source = new SourceSpec
            {
                Type = "duckdb",
                Location = path,
                Options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["columns"] = "id, name",
                    ["filter"] = "score >= 100",
                },
            },
            Target = new TargetSpec { Connection = cs, Schema = IntegrationDb.Schema, Table = table },
            Schema = new SchemaPolicy { Evolve = SchemaEvolution.Widen },
            Load = new LoadPolicy(),
        };

        try
        {
            var result = await IntegrationDb.RealRunner().RunAsync(flow);

            Assert.Equal(FlowStatus.Success, result.Status);
            Assert.Equal(2, result.RowsLoaded);                                  // score >= 100 keeps Ann and Cy
            Assert.False(await IntegrationDb.ColumnExistsAsync(cs, table, "score")); // projection dropped the column
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, table, "name"));
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
