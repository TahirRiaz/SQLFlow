using SqlFlow.Core.Ingestion;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Exercises the PreProcessOnTarget / PostProcessOnTarget hooks against the real sink: raw T-SQL run on the
/// target before and after the load, the Length &gt; 2 activation gate, the post-hook failure that does not roll
/// back the committed load (and keeps staging), and the invoke-alias guard that refuses a set-but-unsupported
/// alias rather than silently ignoring it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class IngestionProcessHookIntegrationTests
{
    private static IngestionFlow Flow(int flowId, string src, string trg, ProcessPolicy process)
        => new()
        {
            FlowId = flowId,
            Source = new IngestionSource { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = src } },
            Target = new IngestionTarget { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = trg } },
            Load = new IngestionLoadPolicy { KeyColumns = ["Id"] },
            Process = process,
        };

    [SkippableFact]
    public async Task PreAndPostHooks_RunOnTarget_InOrder()
    {
        // Unique across the integration suite (like Scd2's 9102): staging cleanup is keyed by flow id, so a
        // flow id shared with another class (IncrementalWindow used to share 20/21) lets parallel classes
        // drop each other's live staging.
        const int flowId = 9120;
        var cs = IntegrationDb.Require();
        const string src = "_SfHook_Src";
        const string trg = "_SfHook_Trg";
        const string audit = "_SfHook_Audit";
        await Reset(cs, src, trg, flowId);
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a'),(2,'b');");
        await CreateAuditAndProcs(cs, audit);

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = Flow(flowId, src, trg, new ProcessPolicy
            {
                PreProcessOnTarget = "EXEC [dbo].[usp_SfPreHook]",
                PostProcessOnTarget = "EXEC [dbo].[usp_SfPostHook]",
            });

            var result = await runner.RunAsync(flow);
            Assert.True(result.Success, result.Error);
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, trg));
            Assert.Equal(1, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{audit}] WHERE [Phase] = 'pre'"));
            Assert.Equal(1, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{audit}] WHERE [Phase] = 'post'"));

            var preAt = await IntegrationDb.ScalarAsync<DateTime?>(cs, $"SELECT [At] FROM [dbo].[{audit}] WHERE [Phase] = 'pre'");
            var postAt = await IntegrationDb.ScalarAsync<DateTime?>(cs, $"SELECT [At] FROM [dbo].[{audit}] WHERE [Phase] = 'post'");
            Assert.True(postAt >= preAt, "PostProcess must run after PreProcess.");
        }
        finally
        {
            await Cleanup(cs, src, trg, audit, flowId);
        }
    }

    [SkippableFact]
    public async Task ShortHook_IsSkippedByTheLengthGate()
    {
        const int flowId = 9121;
        var cs = IntegrationDb.Require();
        const string src = "_SfHookGate_Src";
        const string trg = "_SfHookGate_Trg";
        const string audit = "_SfHook_Audit";
        await Reset(cs, src, trg, flowId);
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a');");
        await CreateAuditAndProcs(cs, audit);

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            // "x " trims to one character, which is <= 2, so the gate skips it (no hook runs).
            var flow = Flow(flowId, src, trg, new ProcessPolicy { PreProcessOnTarget = "x " });

            var result = await runner.RunAsync(flow);
            Assert.True(result.Success, result.Error);
            Assert.Equal(0, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{audit}]"));
        }
        finally
        {
            await Cleanup(cs, src, trg, audit, flowId);
        }
    }

    [SkippableFact]
    public async Task PostHookFailure_KeepsCommittedLoad_AndRetainsStaging()
    {
        const int flowId = 9122;
        var cs = IntegrationDb.Require();
        const string src = "_SfHookFail_Src";
        const string trg = "_SfHookFail_Trg";
        await Reset(cs, src, trg, flowId);
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a'),(2,'b');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = Flow(flowId, src, trg, new ProcessPolicy { PostProcessOnTarget = "EXEC [dbo].[usp_SfDoesNotExist]" });

            var result = await runner.RunAsync(flow);
            Assert.False(result.Success);
            Assert.NotNull(result.Error);
            // The load committed before the post-hook ran, so the rows are present.
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, trg));
            // The failure occurred before the drop, so staging is kept for debugging.
            Assert.True(result.StagingRetained);
            Assert.True(await RelationalIngestionHarness.StagingCountAsync(cs, flowId) >= 1);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, src);
            await IntegrationDb.DropTableAsync(cs, trg);
            await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
        }
    }

    [SkippableFact]
    public async Task InvokeAlias_Set_ButUnsupported_FailsClearly()
    {
        const int flowId = 9123;
        var cs = IntegrationDb.Require();
        const string src = "_SfHookInv_Src";
        const string trg = "_SfHookInv_Trg";
        await Reset(cs, src, trg, flowId);
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = Flow(flowId, src, trg, new ProcessPolicy { PreInvokeAlias = "RefreshAdfPipeline" });

            var result = await runner.RunAsync(flow);
            Assert.False(result.Success);
            Assert.Contains("invoke subsystem", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, src);
            await IntegrationDb.DropTableAsync(cs, trg);
            await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
        }
    }

    private static async Task CreateAuditAndProcs(string cs, string audit)
    {
        await IntegrationDb.ExecuteAsync(cs, $"DROP TABLE IF EXISTS [dbo].[{audit}];");
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{audit}] ([Phase] nvarchar(10) NOT NULL, [At] datetime2(3) NOT NULL);");
        await IntegrationDb.ExecuteAsync(cs, $"CREATE OR ALTER PROCEDURE [dbo].[usp_SfPreHook] AS INSERT INTO [dbo].[{audit}]([Phase],[At]) VALUES ('pre', SYSUTCDATETIME());");
        await IntegrationDb.ExecuteAsync(cs, $"CREATE OR ALTER PROCEDURE [dbo].[usp_SfPostHook] AS INSERT INTO [dbo].[{audit}]([Phase],[At]) VALUES ('post', SYSUTCDATETIME());");
    }

    private static async Task Reset(string cs, string src, string trg, int flowId)
    {
        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{src}] ([Id] int NOT NULL, [Val] nvarchar(20) NULL);");
    }

    private static async Task Cleanup(string cs, string src, string trg, string audit, int flowId)
    {
        await IntegrationDb.ExecuteAsync(cs, "DROP PROCEDURE IF EXISTS [dbo].[usp_SfPreHook];");
        await IntegrationDb.ExecuteAsync(cs, "DROP PROCEDURE IF EXISTS [dbo].[usp_SfPostHook];");
        await IntegrationDb.ExecuteAsync(cs, $"DROP TABLE IF EXISTS [dbo].[{audit}];");
        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
    }
}
