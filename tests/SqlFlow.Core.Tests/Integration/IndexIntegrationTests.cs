using SqlFlow.Core.Model;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Index synchronization against the physical sink database, proving the consolidation end to end:
/// a newly created table gets its declared (desired) indexes built; a pre-existing table has its
/// non-clustered indexes disabled for the load and rebuilt afterwards. Each test drops its table up
/// front so it starts fresh.
/// </summary>
[Trait("Category", "Integration")]
public sealed class IndexIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_itx_" + Guid.NewGuid().ToString("N"));

    public IndexIntegrationTests() => Directory.CreateDirectory(_dir);

    private SourceSpec Csv(string fileName, string content)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, content);
        return new SourceSpec { Type = "csv", Location = path, Options = new Dictionary<string, string?>() };
    }

    private static FlowDefinition Flow(string cs, SourceSpec source, string table, string? desiredIndexes = null, bool manageIndexes = false)
        => new()
        {
            Name = table,
            Source = source,
            Target = new TargetSpec { Connection = cs, Schema = IntegrationDb.Schema, Table = table },
            Schema = new SchemaPolicy { Evolve = SchemaEvolution.Widen },
            Load = new LoadPolicy { ManageIndexes = manageIndexes },
            DesiredIndexes = desiredIndexes,
        };

    [SkippableFact]
    public async Task NewTable_BuildsDesiredIndex()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_Desired_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        var index = "IX_" + table + "_OrderId";
        var script = $"CREATE NONCLUSTERED INDEX [{index}] ON [{IntegrationDb.Schema}].[{table}] ([OrderId]);";
        var source = Csv("orders.csv", "OrderId,Customer\n1,Acme\n2,Globex\n");

        try
        {
            var result = await IntegrationDb.RealRunner().RunAsync(Flow(cs, source, table, desiredIndexes: script));

            Assert.Equal(FlowStatus.Success, result.Status);
            Assert.True(await IntegrationDb.IndexExistsAsync(cs, table, index), "desired index should exist on the new table");
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, table));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task ExistingTable_KeepsIndexEnabled_AfterDisableRebuildLoad()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_Rebuild_" + Guid.NewGuid().ToString("N")[..8];
        var index = "IX_" + table + "_OrderId";
        await IntegrationDb.DropTableAsync(cs, table);

        try
        {
            // Pre-create the table with a non-clustered index so the table is "existing" on the run.
            await IntegrationDb.ExecuteAsync(cs,
                $"CREATE TABLE [{IntegrationDb.Schema}].[{table}] ([OrderId] varchar(255) NULL, [Customer] varchar(255) NULL);");
            await IntegrationDb.ExecuteAsync(cs,
                $"CREATE NONCLUSTERED INDEX [{index}] ON [{IntegrationDb.Schema}].[{table}] ([OrderId]);");

            var source = Csv("orders.csv", "OrderId,Customer\n1,Acme\n2,Globex\n3,Initech\n");
            var result = await IntegrationDb.RealRunner().RunAsync(Flow(cs, source, table, manageIndexes: true));

            Assert.Equal(FlowStatus.Success, result.Status);
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, table));
            Assert.True(await IntegrationDb.IndexExistsAsync(cs, table, index), "index should still exist after the load");
            Assert.False(await IntegrationDb.IndexIsDisabledAsync(cs, table, index), "index should be rebuilt (enabled) after the load");
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
