using SqlFlow.Core.Model;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// End-to-end JSON ingestion against the physical sink, proving the path-based flattener rides the same
/// engine path as CSV and XLS: records are flattened to raw string columns, unioned across files, given
/// the provenance columns, and bulk-loaded into a real table. Each test drops its table up front so it
/// always starts fresh.
/// </summary>
[Trait("Category", "Integration")]
public sealed class JsonIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_jsonit_" + Guid.NewGuid().ToString("N"));

    public JsonIntegrationTests() => Directory.CreateDirectory(_dir);

    private string Write(string fileName, string content)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private static FlowDefinition Flow(string table, string connection, string location, Dictionary<string, string?>? options = null)
        => new()
        {
            Name = table,
            Source = new SourceSpec { Type = "json", Location = location, Options = options ?? new Dictionary<string, string?>() },
            Target = new TargetSpec { Connection = connection, Schema = IntegrationDb.Schema, Table = table },
            Schema = new SchemaPolicy { Evolve = SchemaEvolution.Widen, DefaultColumnType = "nvarchar(4000)" },
            Load = new LoadPolicy { Mode = LoadMode.TruncateLoad },
        };

    [SkippableFact]
    public async Task JsonArray_FlattensNestedRecordsIntoSink()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_Json_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        var path = Write("orders.json",
            """
            [
              { "id": 1, "customer": { "name": "Ann" }, "amount": 9.50 },
              { "id": 2, "customer": { "name": "Bob" }, "amount": 3.00 },
              { "id": 3, "customer": { "name": "Cy" },  "amount": 7.25 }
            ]
            """);

        try
        {
            var result = await IntegrationDb.RealRunner().RunAsync(Flow(table, cs, path));

            Assert.Equal(FlowStatus.Success, result.Status);
            Assert.Equal(3, result.RowsLoaded);
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, table));
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, table, "customer_name"));
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, table, "FileName_DW"));
            Assert.Equal("Bob", await IntegrationDb.ScalarAsync<string?>(cs,
                $"SELECT [customer_name] FROM [dbo].[{table}] WHERE [id] = '2'"));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task Ndjson_LoadsOneRowPerLine()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_Ndjson_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        var path = Write("events.ndjson",
            "{ \"id\": 1, \"kind\": \"click\" }\n{ \"id\": 2, \"kind\": \"view\" }\n{ \"id\": 3, \"kind\": \"buy\" }\n");

        try
        {
            var result = await IntegrationDb.RealRunner().RunAsync(Flow(table, cs, path));

            Assert.Equal(FlowStatus.Success, result.Status);
            Assert.Equal(3, result.RowsLoaded);
            Assert.Equal("buy", await IntegrationDb.ScalarAsync<string?>(cs,
                $"SELECT [kind] FROM [dbo].[{table}] WHERE [id] = '3'"));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task RootPathAndJsonPaths_KeepNestedPayloadAsOneColumn()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_JsonPayload_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        var path = Write("env.json",
            """
            {
              "meta": { "version": 2 },
              "data": { "records": [
                { "id": 1, "payload": { "a": 1, "b": [2, 3] } },
                { "id": 2, "payload": { "a": 9, "b": [] } }
              ] }
            }
            """);

        var options = new Dictionary<string, string?>
        {
            ["rootPath"] = "$.data.records",
            ["jsonPaths"] = "$.payload",
        };

        try
        {
            var result = await IntegrationDb.RealRunner().RunAsync(Flow(table, cs, path, options));

            Assert.Equal(FlowStatus.Success, result.Status);
            Assert.Equal(2, result.RowsLoaded);
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, table, "payload"));
            Assert.False(await IntegrationDb.ColumnExistsAsync(cs, table, "payload_a"));
            Assert.Equal("{\"a\":1,\"b\":[2,3]}", await IntegrationDb.ScalarAsync<string?>(cs,
                $"SELECT [payload] FROM [dbo].[{table}] WHERE [id] = '1'"));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task SchemaEvolution_UnionsColumnsAcrossFiles()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_JsonEvolve_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        Write("jan.json", """{ "id": 1, "amount": 9.5 }""");
        Write("feb.json", """{ "id": 2, "country": "Norway" }""");
        var options = new Dictionary<string, string?> { ["srcFile"] = "*.json" };

        try
        {
            var result = await IntegrationDb.RealRunner().RunAsync(Flow(table, cs, _dir, options));

            Assert.Equal(FlowStatus.Success, result.Status);
            Assert.Equal(2, result.RowsLoaded);
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, table, "amount"));
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, table, "country"));
            // The file lacking a column null-fills it.
            Assert.Null(await IntegrationDb.ScalarAsync<string?>(cs,
                $"SELECT [country] FROM [dbo].[{table}] WHERE [id] = '1'"));
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
