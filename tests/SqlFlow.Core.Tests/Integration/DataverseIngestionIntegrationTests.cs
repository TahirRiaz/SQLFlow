using System.Net;
using SqlFlow.Core.Model;
using SqlFlow.Tests.Dataverse;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// A Power Apps table through the real engine into the sink: the reader fetches from a scripted Dataverse environment
/// and the file flow's load, schema evolution and watermark do the rest. Proves a column added or widened in Power Apps
/// reaches the target, an incremental run asks the service only for what changed, a full reload reconciles deleted rows,
/// and a load that fails part way resumes exactly after the rows it committed.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DataverseIngestionIntegrationTests
{
    private static FlowDefinition Flow(string table, SourceSpec source, LoadPolicy? load = null, IncrementalSpec? incremental = null)
        => new()
        {
            Name = table,
            Source = source,
            Target = new TargetSpec { Connection = IntegrationDb.Require(), Schema = IntegrationDb.Schema, Table = table },
            Schema = new SchemaPolicy { Evolve = SchemaEvolution.Widen },
            Load = load ?? new LoadPolicy(),
            Incremental = incremental,
        };

    private static string NewTable(string prefix) => $"IT_Dv{prefix}_" + Guid.NewGuid().ToString("N")[..8];

    [SkippableFact]
    public async Task A_table_lands_typed_and_columns_added_widened_or_removed_in_power_apps_follow()
    {
        var cs = IntegrationDb.Require();
        var table = NewTable("Shape");
        var service = DataverseTesting.Accounts(5);
        var runner = IntegrationDb.RealRunner(DataverseTesting.Reader(service));

        // The flow as an author writes it, through the YAML loader: the connection is the sink's own reference.
        var yaml = $$"""
            name: {{table}}
            source:
              type: dataverse
              location: {{FakeDataverse.Environment}}
              options:
                table: account
            target:
              connection: ${env:SQLFlowSinkConStr}
              schema: {{IntegrationDb.Schema}}
              table: {{table}}
            load:
              mode: truncateLoad
            """;
        var flow = new YamlFlowLoader().Parse(yaml, table + ".yaml");

        try
        {
            var first = await runner.RunAsync(flow);
            Assert.True(first.Status == FlowStatus.Success, first.Error);
            Assert.Equal(5, await IntegrationDb.RowCountAsync(cs, table));
            Assert.Equal("uniqueidentifier", await IntegrationDb.ColumnTypeAsync(cs, table, "accountid"));
            Assert.Equal("nvarchar(160)", await IntegrationDb.ColumnTypeAsync(cs, table, "name"));
            Assert.Equal("nvarchar(2000)", await IntegrationDb.ColumnTypeAsync(cs, table, "description"));
            Assert.Equal("decimal", await IntegrationDb.ColumnTypeAsync(cs, table, "revenue"));
            Assert.Equal("bigint", await IntegrationDb.ColumnTypeAsync(cs, table, "versionnumber"));
            Assert.Equal("datetime2", await IntegrationDb.ColumnTypeAsync(cs, table, "modifiedon"));
            Assert.Equal("uniqueidentifier", await IntegrationDb.ColumnTypeAsync(cs, table, "parentaccountid"));
            Assert.Equal(5001.25m, await IntegrationDb.ScalarAsync<decimal>(cs, $"SELECT revenue FROM [{IntegrationDb.Schema}].[{table}] WHERE name = 'Account 5'"));
            Assert.Equal(4, await IntegrationDb.ScalarAsync<int>(cs, $"SELECT CONVERT(int, scale) FROM sys.columns WHERE object_id = OBJECT_ID('[{IntegrationDb.Schema}].[{table}]') AND name = 'revenue'"));

            // A maker adds a column and lengthens another: the next run widens the target and fills the new column.
            service.Attributes.Add(new FakeAttribute("cr6b2_rating", "IntegerType"));
            var name = service.Attributes.FindIndex(a => a.LogicalName == "name");
            service.Attributes[name] = service.Attributes[name] with { MaxLength = 300 };
            foreach (var row in service.Rows)
            {
                row["cr6b2_rating"] = 4;
            }

            service.Rows[0]["name"] = new string('n', 280);

            var second = await runner.RunAsync(flow);
            Assert.True(second.Status == FlowStatus.Success, second.Error);
            Assert.Equal("int", await IntegrationDb.ColumnTypeAsync(cs, table, "cr6b2_rating"));
            Assert.Equal("nvarchar(300)", await IntegrationDb.ColumnTypeAsync(cs, table, "name"));
            Assert.Equal(5, await IntegrationDb.ScalarAsync<int>(cs, $"SELECT COUNT(*) FROM [{IntegrationDb.Schema}].[{table}] WHERE cr6b2_rating = 4"));

            // A maker removes a column: the target keeps it, and the rows loaded after carry no value in it.
            service.Attributes.RemoveAll(a => a.LogicalName == "description");
            var third = await runner.RunAsync(flow);
            Assert.True(third.Status == FlowStatus.Success, third.Error);
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, table, "description"));
            Assert.Equal(0, await IntegrationDb.ScalarAsync<int>(cs, $"SELECT COUNT(*) FROM [{IntegrationDb.Schema}].[{table}] WHERE description IS NOT NULL"));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task An_incremental_run_asks_only_for_what_changed_and_a_quiet_one_loads_nothing()
    {
        var cs = IntegrationDb.Require();
        var table = NewTable("Inc");
        var service = DataverseTesting.Accounts(6);
        var runner = IntegrationDb.RealRunner(DataverseTesting.Reader(service));
        var incremental = new IncrementalSpec { WatermarkColumn = "versionnumber" };
        var flow = Flow(table, DataverseTesting.Spec(), incremental: incremental);
        await IntegrationDb.DropTableAsync(cs, table);

        try
        {
            var first = await runner.RunAsync(flow);
            Assert.True(first.Status == FlowStatus.Success, first.Error);
            Assert.Equal(6, await IntegrationDb.RowCountAsync(cs, table));
            Assert.Contains("$orderby=versionnumber asc,accountid asc", Uri.UnescapeDataString(service.RowRequests[^1].Url.Query), StringComparison.Ordinal);

            // Two rows change in Power Apps (their row version moves on) and one is created.
            service.Rows[1]["name"] = "Account 2 renamed";
            service.Rows[1]["versionnumber"] = 638_183_964_640_917_000L;
            service.Rows[3]["versionnumber"] = 638_183_964_640_917_001L;
            var created = DataverseTesting.Account(7);
            created["versionnumber"] = 638_183_964_640_917_002L;
            service.Rows.Add(created);

            var before = service.RowRequests.Count;
            var second = await runner.RunAsync(flow);
            Assert.True(second.Status == FlowStatus.Success, second.Error);
            Assert.Equal(3, second.RowsLoaded);
            Assert.Contains("versionnumber gt 638183964640916102", Uri.UnescapeDataString(service.RowRequests[before].Url.Query), StringComparison.Ordinal);
            Assert.Equal(1, await IntegrationDb.ScalarAsync<int>(cs, $"SELECT COUNT(*) FROM [{IntegrationDb.Schema}].[{table}] WHERE name = 'Account 2 renamed'"));

            var third = await runner.RunAsync(flow);
            Assert.True(third.Status == FlowStatus.Success, third.Error);
            Assert.Equal(0, third.RowsLoaded);
            Assert.Equal(9, await IntegrationDb.RowCountAsync(cs, table));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task A_full_reload_reconciles_rows_deleted_in_power_apps()
    {
        var cs = IntegrationDb.Require();
        var table = NewTable("Del");
        var service = DataverseTesting.Accounts(5);
        var runner = IntegrationDb.RealRunner(DataverseTesting.Reader(service));
        var flow = Flow(table, DataverseTesting.Spec(), new LoadPolicy { Mode = LoadMode.TruncateLoad });
        await IntegrationDb.DropTableAsync(cs, table);

        try
        {
            Assert.Equal(FlowStatus.Success, (await runner.RunAsync(flow)).Status);
            service.Rows.RemoveAt(4);
            service.Rows.RemoveAt(1);

            var reload = await runner.RunAsync(flow);
            Assert.True(reload.Status == FlowStatus.Success, reload.Error);
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, table));

            // A table emptied in Power Apps empties the target too: an unbounded read of nothing is a load of nothing.
            service.Rows.Clear();
            Assert.Equal(FlowStatus.Success, (await runner.RunAsync(flow)).Status);
            Assert.Equal(0, await IntegrationDb.RowCountAsync(cs, table));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task A_load_that_fails_part_way_resumes_exactly_after_what_it_committed()
    {
        var cs = IntegrationDb.Require();
        var table = NewTable("Resume");
        var service = DataverseTesting.Accounts(8);

        // Row versions run against the ids, so reading in id order and reading in version order commit different rows.
        for (var i = 0; i < service.Rows.Count; i++)
        {
            service.Rows[i]["versionnumber"] = 638_183_964_640_916_200L - i;
        }

        // The third page fails outright on the first run only.
        service.Fail(url => FakeDataverse.IsRowRequest(url) && url.Query.Contains("$skiptoken=4", StringComparison.Ordinal),
            HttpStatusCode.BadRequest, FakeDataverse.ODataError("Simulated failure while reading."));

        var runner = IntegrationDb.RealRunner(DataverseTesting.Reader(service));
        var flow = Flow(
            table,
            DataverseTesting.Spec(("pageSize", "2")),
            new LoadPolicy { BatchSize = 2 },
            new IncrementalSpec { WatermarkColumn = "versionnumber" });
        await IntegrationDb.DropTableAsync(cs, table);

        try
        {
            var failed = await runner.RunAsync(flow);
            Assert.Equal(FlowStatus.Failed, failed.Status);
            Assert.Contains("Simulated failure while reading", failed.Error, StringComparison.Ordinal);

            // What the failed run committed is a prefix of the table in row-version order, never a scattering of it.
            var committed = await IntegrationDb.RowCountAsync(cs, table);
            Assert.InRange(committed, 2, 4);
            var lowest = service.Rows.Select(r => (long)r["versionnumber"]!).Order().Take((int)committed).Max();
            Assert.Equal(lowest, await IntegrationDb.ScalarAsync<long>(cs, $"SELECT MAX(versionnumber) FROM [{IntegrationDb.Schema}].[{table}]"));

            var resumed = await runner.RunAsync(flow);
            Assert.True(resumed.Status == FlowStatus.Success, resumed.Error);
            Assert.Equal(8, await IntegrationDb.RowCountAsync(cs, table));
            Assert.Equal(8, await IntegrationDb.ScalarAsync<int>(cs, $"SELECT COUNT(DISTINCT accountid) FROM [{IntegrationDb.Schema}].[{table}]"));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }
}
