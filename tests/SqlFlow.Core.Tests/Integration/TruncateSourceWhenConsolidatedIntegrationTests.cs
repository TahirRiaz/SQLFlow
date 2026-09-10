using SqlFlow.Core.Ingestion;
using SqlFlow.SqlServer.Ingestion;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// End-to-end coverage of load.truncateSourceWhenConsolidated on the silver (flowType: ing) layer: after a
/// successful load the upstream landing ("pre") table is emptied ONLY once the target has caught up
/// (MAX(watermark) target >= MAX(watermark) landing). The cases below exercise the full lifecycle a silver flow
/// sees: a caught-up target reclaiming the landing table (through a real v_ view), a target that has NOT caught up
/// keeping it, an empty landing being a no-op, a FAILED run leaving the landing intact and a clean retry then
/// reclaiming it (the core "silver fails, bronze is preserved" guarantee), a multi-round steady state where each
/// incremental round reclaims only the consolidated delta while the target accumulates, a non-numeric (datetime2)
/// watermark, and the run-start guards. Each test owns uniquely named objects and drops them up front; the whole
/// class skips when the sink is unreachable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TruncateSourceWhenConsolidatedIntegrationTests
{
    private static RelationalObject Obj(string name) => new() { Database = "db", Schema = "dbo", Name = name };

    private static IngestionFlow BuildFlow(
        int flowId, string source, string target,
        string watermark = "FileDate_DW",
        bool skipInsertNew = false,
        bool withWatermark = true,
        string? preProcess = null,
        int overlapDays = 0) => new()
    {
        FlowId = flowId,
        Source = new IngestionSource { Server = "sink", Table = Obj(source) },
        Target = new IngestionTarget { Server = "sink", Table = Obj(target) },
        Load = new IngestionLoadPolicy
        {
            KeyColumns = ["Id"],
            SkipInsertNew = skipInsertNew,
            TruncateSourceWhenConsolidated = true,
        },
        Incremental = withWatermark
            ? new IncrementalPolicy { Columns = [watermark], OverlapDays = overlapDays }
            : new IncrementalPolicy(),
        Process = preProcess is null ? new ProcessPolicy() : new ProcessPolicy { PreProcessOnTarget = preProcess },
    };

    private static async Task CreateLandingWithView(string cs, string landing, string view, string wmColumn, string wmType, string values)
    {
        await IntegrationDb.ExecuteAsync(cs, $"DROP VIEW IF EXISTS [dbo].[{view}];");
        await IntegrationDb.DropTableAsync(cs, landing);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{landing}] ([Id] int NOT NULL, [{wmColumn}] {wmType} NULL);");
        if (values.Length > 0)
        {
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{landing}] ([Id],[{wmColumn}]) VALUES {values};");
        }

        await IntegrationDb.ExecuteAsync(cs, $"CREATE VIEW [dbo].[{view}] AS SELECT [Id],[{wmColumn}] FROM [dbo].[{landing}];");
    }

    private static async Task DropChain(string cs, string landing, string view, string trg, int flowId)
    {
        if (view.Length > 0)
        {
            await IntegrationDb.ExecuteAsync(cs, $"DROP VIEW IF EXISTS [dbo].[{view}];");
        }

        await IntegrationDb.DropTableAsync(cs, landing);
        await IntegrationDb.DropTableAsync(cs, trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
    }

    private static bool TruncatedLandingInTrace(IngestionRunResult result, string landing)
        => result.SqlTrace.Any(e =>
            e.Sql.Contains("TRUNCATE TABLE", StringComparison.OrdinalIgnoreCase)
            && e.Sql.Contains(landing, StringComparison.OrdinalIgnoreCase));

    [SkippableFact]
    public async Task Landing_IsTruncated_WhenTargetHasCaughtUp()
    {
        var cs = IntegrationDb.Require();
        const int flowId = 941;
        const string landing = "SfConsolLanding1";
        const string view = "v_SfConsolLanding1";   // "v_" is stripped to the landing table
        const string trg = "SfConsolTarget1";
        await DropChain(cs, landing, view, trg, flowId);
        await CreateLandingWithView(cs, landing, view, "FileDate_DW", "decimal(14,0)", "(1,20240101),(2,20240102),(3,20240103)");

        try
        {
            // Empty target: the full load consolidates every landed row, so target MAX == landing MAX and the
            // landing table is reclaimed.
            var result = await RelationalIngestionHarness.BuildRunner().RunAsync(BuildFlow(flowId, view, trg));
            Assert.True(result.Success, result.Error);

            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, trg));
            Assert.Equal(0, await IntegrationDb.RowCountAsync(cs, landing));   // truncated
            Assert.True(TruncatedLandingInTrace(result, landing));            // via the source.truncate step
        }
        finally
        {
            await DropChain(cs, landing, view, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task Landing_IsRetained_WhenTargetHasNotCaughtUp()
    {
        var cs = IntegrationDb.Require();
        const int flowId = 942;
        const string landing = "SfConsolLanding2";
        const string trg = "SfConsolTarget2";
        await DropChain(cs, landing, "", trg, flowId);

        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{landing}] ([Id] int NOT NULL, [FileDate_DW] decimal(14,0) NULL);");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{landing}] VALUES (1,20240101),(2,20240102),(3,20240103);");

        try
        {
            // skipInsertNew against an empty target consolidates nothing: the target stays empty, so its MAX has
            // NOT caught up to the landing table's, and the landing rows must be preserved.
            var result = await RelationalIngestionHarness.BuildRunner().RunAsync(BuildFlow(flowId, landing, trg, skipInsertNew: true));
            Assert.True(result.Success, result.Error);

            Assert.Equal(0, await IntegrationDb.RowCountAsync(cs, trg));
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, landing));   // retained, not truncated
            Assert.False(TruncatedLandingInTrace(result, landing));
        }
        finally
        {
            await DropChain(cs, landing, "", trg, flowId);
        }
    }

    [SkippableFact]
    public async Task EmptyLanding_IsNoOp_AndSucceeds()
    {
        var cs = IntegrationDb.Require();
        const int flowId = 944;
        const string landing = "SfConsolLanding4";
        const string view = "v_SfConsolLanding4";
        const string trg = "SfConsolTarget4";
        await DropChain(cs, landing, view, trg, flowId);
        await CreateLandingWithView(cs, landing, view, "FileDate_DW", "decimal(14,0)", "");   // no rows

        try
        {
            var result = await RelationalIngestionHarness.BuildRunner().RunAsync(BuildFlow(flowId, view, trg));
            Assert.True(result.Success, result.Error);

            // An empty landing table is a no-op: nothing to truncate, the table is left in place, and the run
            // still succeeds.
            Assert.True(await IntegrationDb.TableExistsAsync(cs, landing));
            Assert.Equal(0, await IntegrationDb.RowCountAsync(cs, landing));
            Assert.False(TruncatedLandingInTrace(result, landing));
        }
        finally
        {
            await DropChain(cs, landing, view, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task Landing_IsRetained_WhenRunFails_ThenTruncated_OnCleanRetry()
    {
        var cs = IntegrationDb.Require();
        const int flowId = 945;
        const string landing = "SfConsolLanding5";
        const string view = "v_SfConsolLanding5";
        const string trg = "SfConsolTarget5";
        await DropChain(cs, landing, view, trg, flowId);
        await CreateLandingWithView(cs, landing, view, "FileDate_DW", "decimal(14,0)", "(1,20240101),(2,20240102),(3,20240103)");

        try
        {
            // A silver run that fails (here a pre-process that raises before the load) must NOT truncate the
            // landing table: the data has not been consolidated, so it has to survive for the retry.
            var failing = await RelationalIngestionHarness.BuildRunner()
                .RunAsync(BuildFlow(flowId, view, trg, preProcess: "RAISERROR('silver failure injected by test',16,1);"));
            Assert.False(failing.Success);
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, landing));   // preserved through the failure
            Assert.False(await IntegrationDb.TableExistsAsync(cs, trg));       // nothing consolidated

            // The clean retry consolidates and only then reclaims the landing table.
            var retry = await RelationalIngestionHarness.BuildRunner().RunAsync(BuildFlow(flowId, view, trg));
            Assert.True(retry.Success, retry.Error);
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, trg));
            Assert.Equal(0, await IntegrationDb.RowCountAsync(cs, landing));   // now reclaimed
        }
        finally
        {
            await DropChain(cs, landing, view, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task SteadyState_ReclaimsLandingEachRound_AndTargetAccumulates()
    {
        var cs = IntegrationDb.Require();
        const int flowId = 946;
        const string landing = "SfConsolLanding6";
        const string view = "v_SfConsolLanding6";
        const string trg = "SfConsolTarget6";
        await DropChain(cs, landing, view, trg, flowId);
        await CreateLandingWithView(cs, landing, view, "FileDate_DW", "decimal(14,0)", "(1,20240101),(2,20240102)");

        try
        {
            // Round 1: the first batch lands, is consolidated in full (empty target), and the landing is reclaimed.
            var round1 = await RelationalIngestionHarness.BuildRunner().RunAsync(BuildFlow(flowId, view, trg));
            Assert.True(round1.Success, round1.Error);
            Assert.Equal(2, round1.RowsStaged);
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, trg));
            Assert.Equal(0, await IntegrationDb.RowCountAsync(cs, landing));

            // A new pre load appends the next batch into the (now empty) landing table.
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{landing}] VALUES (3,20240103),(4,20240104);");

            // Round 2: the incremental read pulls ONLY the delta (2 rows, not 4), the target accumulates to 4, and
            // the landing is reclaimed again. This is the steady state the feature exists for: the landing never
            // grows unbounded and never holds more than the un-consolidated delta.
            var round2 = await RelationalIngestionHarness.BuildRunner().RunAsync(BuildFlow(flowId, view, trg));
            Assert.True(round2.Success, round2.Error);
            Assert.Equal(2, round2.RowsStaged);                               // incremental: only the new rows
            Assert.Equal(4, await IntegrationDb.RowCountAsync(cs, trg));      // 1..4 accumulated
            Assert.Equal(0, await IntegrationDb.RowCountAsync(cs, landing));  // reclaimed
        }
        finally
        {
            await DropChain(cs, landing, view, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task Landing_IsTruncated_WithDatetimeWatermark()
    {
        var cs = IntegrationDb.Require();
        const int flowId = 947;
        const string landing = "SfConsolLanding7";
        const string view = "v_SfConsolLanding7";
        const string trg = "SfConsolTarget7";
        await DropChain(cs, landing, view, trg, flowId);
        await CreateLandingWithView(cs, landing, view, "WatermarkDate", "datetime2(3)",
            "(1,'2024-01-01T08:00:00'),(2,'2024-01-02T09:30:00')");

        try
        {
            // The gate compares a datetime2 high-water mark (the non-numeric IComparable path), not just decimals.
            var result = await RelationalIngestionHarness.BuildRunner()
                .RunAsync(BuildFlow(flowId, view, trg, watermark: "WatermarkDate"));
            Assert.True(result.Success, result.Error);

            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, trg));
            Assert.Equal(0, await IntegrationDb.RowCountAsync(cs, landing));   // truncated
            Assert.True(TruncatedLandingInTrace(result, landing));
        }
        finally
        {
            await DropChain(cs, landing, view, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task Run_Fails_WhenTruncateSourceSetWithoutWatermark()
    {
        var cs = IntegrationDb.Require();
        const int flowId = 943;
        const string landing = "SfConsolLanding3";
        const string trg = "SfConsolTarget3";

        // The guard fires before any source introspection, so no tables are needed; assert the run is rejected
        // with the watermark-required message rather than silently doing nothing.
        var result = await RelationalIngestionHarness.BuildRunner()
            .RunAsync(BuildFlow(flowId, landing, trg, withWatermark: false));

        Assert.False(result.Success);
        Assert.Contains("requires an incremental watermark", result.Error);
    }
}
