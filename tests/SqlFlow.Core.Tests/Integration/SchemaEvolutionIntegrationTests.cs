using SqlFlow.Core.Model;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Schema evolution against the physical sink: a process can ingest different versions of the same dataset
/// over time. New fields in a later run automatically grow the existing target table (Widen), older rows
/// null-fill the new columns, and path aliases coalesce a renamed field into one always-populated column.
/// Each test drops its table up front for a fresh start.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SchemaEvolutionIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_evoit_" + Guid.NewGuid().ToString("N"));

    public SchemaEvolutionIntegrationTests() => Directory.CreateDirectory(_dir);

    private string Write(string fileName, string content)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private static FlowDefinition Flow(string table, string connection, string location, LoadMode mode, Dictionary<string, string?>? options = null)
        => new()
        {
            Name = table,
            Source = new SourceSpec { Type = "json", Location = location, Options = options ?? new Dictionary<string, string?>() },
            Target = new TargetSpec { Connection = connection, Schema = IntegrationDb.Schema, Table = table },
            Schema = new SchemaPolicy { Evolve = SchemaEvolution.Widen, DefaultColumnType = "nvarchar(4000)" },
            Load = new LoadPolicy { Mode = mode },
        };

    [SkippableFact]
    public async Task NewFieldsInALaterRun_AutoGrowTheExistingTable()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_Evolve_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        var v1 = Write("v1.json", """[ { "id": 1, "name": "Ann" } ]""");
        var v2 = Write("v2.json", """[ { "id": 2, "name": "Bob", "email": "bob@x.io", "status": "active" } ]""");

        try
        {
            // Run 1 creates the table with the v1 shape.
            var run1 = await IntegrationDb.RealRunner().RunAsync(Flow(table, cs, v1, LoadMode.Append));
            Assert.Equal(FlowStatus.Success, run1.Status);
            Assert.False(await IntegrationDb.ColumnExistsAsync(cs, table, "email"));

            // Run 2 carries new fields; Widen adds them to the existing table rather than failing.
            var run2 = await IntegrationDb.RealRunner().RunAsync(Flow(table, cs, v2, LoadMode.Append));
            Assert.Equal(FlowStatus.Success, run2.Status);

            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, table, "email"));    // new column popped in
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, table, "status"));
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, table));             // append kept run 1's row

            // The older row null-fills the new columns; the newer row carries them.
            Assert.Null(await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [email] FROM [dbo].[{table}] WHERE [id] = '1'"));
            Assert.Equal("bob@x.io", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [email] FROM [dbo].[{table}] WHERE [id] = '2'"));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task PathAliases_RenamedFieldAcrossFiles_LoadIntoOneColumn()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_EvolveAlias_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        Write("v1.json", """[ { "id": 1, "name": "Ann" } ]""");
        Write("v2.json", """[ { "id": 2, "fullName": "Bob" } ]""");
        var options = new Dictionary<string, string?>
        {
            ["srcFile"] = "*.json",
            ["pathAliases"] = "person_name=$.name|$.fullName",
        };

        try
        {
            var result = await IntegrationDb.RealRunner().RunAsync(Flow(table, cs, _dir, LoadMode.TruncateLoad, options));

            Assert.Equal(FlowStatus.Success, result.Status);
            Assert.Equal(2, result.RowsLoaded);
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, table, "person_name"));
            Assert.False(await IntegrationDb.ColumnExistsAsync(cs, table, "fullName"));
            // Both versions land in one column, neither null.
            Assert.Equal("Ann", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [person_name] FROM [dbo].[{table}] WHERE [id] = '1'"));
            Assert.Equal("Bob", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [person_name] FROM [dbo].[{table}] WHERE [id] = '2'"));
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
