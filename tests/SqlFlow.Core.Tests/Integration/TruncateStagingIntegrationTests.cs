using SqlFlow.Core.Ingestion;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Verifies TruncatePreTableOnCompletion end-to-end: when the flow's canonical staging table is kept, this flag
/// empties it after a successful load (structure without the run's data), and when the flag is off the kept table
/// retains its rows. Skips when the sink is unreachable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TruncateStagingIntegrationTests
{
    private static IngestionFlow BuildFlow(int flowId, string src, string trg, bool truncateStaging) => new()
    {
        FlowId = flowId,
        Source = new IngestionSource { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = src } },
        Target = new IngestionTarget { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = trg } },
        Load = new IngestionLoadPolicy
        {
            KeyColumns = ["Id"],
            KeepStagingTable = true,
            TruncatePreTableOnCompletion = truncateStaging,
        },
    };

    private static async Task<long?> StagingRowCountAsync(string cs, int flowId)
    {
        var name = await IntegrationDb.ScalarAsync<string?>(cs,
            "SELECT TOP 1 t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id " +
            $"WHERE s.name = 'raw' AND t.name LIKE '%[_]{flowId}' AND t.name NOT LIKE 'mkey[_]%' ORDER BY t.create_date DESC");
        if (name is null)
        {
            return null;
        }

        return await IntegrationDb.ScalarAsync<long>(cs, $"SELECT COUNT_BIG(*) FROM [raw].[{name}]");
    }

    [SkippableFact]
    public async Task KeptStaging_IsTruncated_WhenFlagSet()
    {
        var cs = IntegrationDb.Require();
        const int flowId = 931;
        const string src = "_SfTruncStg_Src1";
        const string trg = "_SfTruncStg_Trg1";
        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, flowId);

        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{src}] ([Id] int NOT NULL, [Name] nvarchar(50) NULL);");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a'),(2,'b'),(3,'c');");

        try
        {
            var result = await RelationalIngestionHarness.BuildRunner().RunAsync(BuildFlow(flowId, src, trg, truncateStaging: true));
            Assert.True(result.Success, result.Error);

            // Staging is kept (exactly one table) but emptied.
            Assert.Equal(1, await RelationalIngestionHarness.StagingCountAsync(cs, flowId));
            Assert.Equal(0, await StagingRowCountAsync(cs, flowId));
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, trg));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, src);
            await IntegrationDb.DropTableAsync(cs, trg);
            await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
        }
    }

    [SkippableFact]
    public async Task KeptStaging_RetainsRows_WhenFlagUnset()
    {
        var cs = IntegrationDb.Require();
        const int flowId = 932;
        const string src = "_SfTruncStg_Src2";
        const string trg = "_SfTruncStg_Trg2";
        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, flowId);

        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{src}] ([Id] int NOT NULL, [Name] nvarchar(50) NULL);");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a'),(2,'b'),(3,'c');");

        try
        {
            var result = await RelationalIngestionHarness.BuildRunner().RunAsync(BuildFlow(flowId, src, trg, truncateStaging: false));
            Assert.True(result.Success, result.Error);

            Assert.Equal(1, await RelationalIngestionHarness.StagingCountAsync(cs, flowId));
            Assert.Equal(3, await StagingRowCountAsync(cs, flowId)); // rows retained
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, src);
            await IntegrationDb.DropTableAsync(cs, trg);
            await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
        }
    }
}
