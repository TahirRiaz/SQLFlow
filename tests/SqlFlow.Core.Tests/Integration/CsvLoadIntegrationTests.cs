using SqlFlow.Core.Model;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// End-to-end load tests against the physical sink database: a CSV is read by the real CSV reader and
/// streamed into a real SQL Server table by the real bulk loader, through the real schema/DDL path.
/// Each test drops its own table up front so it always starts from a clean state.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CsvLoadIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_it_" + Guid.NewGuid().ToString("N"));

    public CsvLoadIntegrationTests() => Directory.CreateDirectory(_dir);

    private SourceSpec Csv(string fileName, string content, Dictionary<string, string?>? options = null)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, content);
        return new SourceSpec { Type = "csv", Location = path, Options = options ?? new Dictionary<string, string?>() };
    }

    private static FlowDefinition Flow(string connectionString, SourceSpec source, string table, SchemaEvolution evolve = SchemaEvolution.Widen, LoadMode mode = LoadMode.Append)
        => new()
        {
            Name = table,
            Source = source,
            Target = new TargetSpec { Connection = connectionString, Schema = IntegrationDb.Schema, Table = table },
            Schema = new SchemaPolicy { Evolve = evolve },
            Load = new LoadPolicy { Mode = mode },
        };

    [SkippableFact]
    public async Task Load_CreatesTableAndInsertsRows()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_Load_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        var source = Csv("orders.csv", "OrderId,Customer\n1,Acme\n2,Globex\n3,Initech\n");
        var result = await IntegrationDb.RealRunner().RunAsync(Flow(cs, source, table));

        try
        {
            Assert.Equal(FlowStatus.Success, result.Status);
            Assert.True(await IntegrationDb.TableExistsAsync(cs, table));
            Assert.Equal(3, result.RowsLoaded);
            // The file load is insert-only (SqlBulkCopy), so every loaded row is reported as an insert; this is
            // what the run's inserted count in the catalog/GUI projects from.
            Assert.Equal(3, result.RowsInserted);
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, table));
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, table, "OrderId"));
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, table, "FileName_DW"));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task Load_WidensSchema_WhenSecondFileHasNewColumn()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_Widen_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        try
        {
            await IntegrationDb.RealRunner().RunAsync(Flow(cs, Csv("v1.csv", "OrderId,Amount\n1,9.5\n"), table));
            Assert.False(await IntegrationDb.ColumnExistsAsync(cs, table, "Country"));

            var second = await IntegrationDb.RealRunner().RunAsync(Flow(cs, Csv("v2.csv", "OrderId,Amount,Country\n2,3.0,Norway\n"), table));

            Assert.Equal(FlowStatus.Success, second.Status);
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, table, "Country"));
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, table));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task TruncateLoad_ReplacesPriorRows()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_Trunc_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        try
        {
            await IntegrationDb.RealRunner().RunAsync(Flow(cs, Csv("a.csv", "OrderId\n1\n2\n3\n"), table, mode: LoadMode.TruncateLoad));
            var second = await IntegrationDb.RealRunner().RunAsync(Flow(cs, Csv("b.csv", "OrderId\n9\n"), table, mode: LoadMode.TruncateLoad));

            Assert.Equal(FlowStatus.Success, second.Status);
            Assert.Equal(1, await IntegrationDb.RowCountAsync(cs, table));
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
