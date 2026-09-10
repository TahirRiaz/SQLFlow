using SqlFlow.Core.Invoke;
using SqlFlow.SqlServer.FullMode;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Exercises the invoke subsystem against the real sink. Invoke triggers named external resources only (Azure
/// Data Factory pipelines, Azure Automation runbooks); host code/script execution was removed, so there is no
/// runnable executor in this build and a dispatched invoke fails honestly and is logged. The standalone path
/// proves a flw.Invoke flow loads, dispatches, and records the failed run; the hook path proves a full-mode
/// ingestion flow's PreInvokeAlias actually reaches the dispatcher (the wiring works end to end) and fails the
/// flow before any load when the invoke cannot run.
/// </summary>
[Trait("Category", "Integration")]
public sealed class InvokeFlowIntegrationTests
{
    [SkippableFact]
    public async Task FullMode_LoadsInvokeFlow_Dispatches_AndLogsFailure_NoExecutor()
    {
        const int flowId = 80;
        var cs = IntegrationDb.Require();
        await ControlPlaneSchema.EnsureAsync(cs);
        await CleanInvoke(cs, flowId, "inv-adf");

        try
        {
            await IntegrationDb.ExecuteAsync(cs,
                $"INSERT INTO [flw].[Invoke] ([FlowID],[SysAlias],[InvokeAlias],[InvokeType],[PipelineName]) " +
                $"VALUES ({flowId},'inv','inv-adf','adf','pl_refresh');");

            var host = FullModeIngestionHost.Create(new FullModeOptions { ControlConnectionString = cs });

            var def = await host.InvokeFlows.LoadByIdAsync(flowId);
            Assert.Equal("inv-adf", def.InvokeAlias);
            Assert.Equal(InvokeType.AzureDataFactory, def.InvokeType);
            Assert.Equal("pl_refresh", def.PipelineName);

            var result = await host.RunInvokeByIdAsync(flowId);

            Assert.False(result.Success);
            Assert.Contains("not supported", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            // The failed run is still logged as the legacy InvokeType code 'adf', Success 0.
            Assert.Equal("adf", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [FlowType] FROM [flw].[SysLog] WHERE FlowID = {flowId}"));
            Assert.Equal(0, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT CONVERT(int, [Success]) FROM [flw].[SysLog] WHERE FlowID = {flowId}"));
        }
        finally
        {
            await CleanInvoke(cs, flowId, "inv-adf");
        }
    }

    [SkippableFact]
    public async Task FullMode_Ingestion_PreInvokeAlias_ReachesDispatcher_FailsBeforeLoad()
    {
        const int invokeId = 81;
        const int ingestId = 82;
        var cs = IntegrationDb.Require();
        await ControlPlaneSchema.EnsureAsync(cs);

        const string src = "_SfInvHook_Src";
        const string trg = "_SfInvHook_Trg";

        await CleanInvoke(cs, invokeId, "PreHookAdf");
        await CleanIngestion(cs, ingestId);
        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, ingestId);

        try
        {
            await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{src}] ([Id] int NOT NULL, [Val] nvarchar(20) NULL);");
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a'),(2,'b');");

            await IntegrationDb.ExecuteAsync(cs, "INSERT INTO [flw].[CredentialProfile] ([ProfileAlias],[Mode]) VALUES ('sf_cp_invh','InlineConnectionString');");
            await IntegrationDb.ExecuteAsync(cs, "INSERT INTO [flw].[DataSource] ([Alias],[Kind],[ConnectionRef],[CredentialProfileID]) VALUES ('sf_invh_sink','MSSQL','${env:SQLFlowSinkConStr}',(SELECT [CredentialProfileID] FROM [flw].[CredentialProfile] WHERE [ProfileAlias]='sf_cp_invh'));");
            // OnErrorResume = 0 so the failed (unrunnable) pre-hook fails the flow rather than being tolerated.
            await IntegrationDb.ExecuteAsync(cs,
                $"INSERT INTO [flw].[Invoke] ([FlowID],[SysAlias],[InvokeAlias],[InvokeType],[PipelineName],[OnErrorResume]) " +
                $"VALUES ({invokeId},'inv','PreHookAdf','adf','pl_refresh',0);");
            await IntegrationDb.ExecuteAsync(cs,
                $"INSERT INTO [flw].[Ingestion] ([FlowID],[SysAlias],[srcServer],[srcDBSchTbl],[trgServer],[trgDBSchTbl],[KeyColumns],[PreInvokeAlias]) " +
                $"VALUES ({ingestId},'invh-flow','sf_invh_sink','[db].[dbo].[{src}]','sf_invh_sink','[db].[dbo].[{trg}]','Id','PreHookAdf');");

            var host = FullModeIngestionHost.Create(new FullModeOptions { ControlConnectionString = cs });
            var result = await host.RunFlowByIdAsync(ingestId);

            // The pre-hook reached the dispatcher (proving full-mode hook wiring) and, with no executor, failed
            // the flow before any data work: the target was never created.
            Assert.False(result.Success);
            Assert.Contains("not supported", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Null(await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT OBJECT_ID('dbo.{trg}','U')"));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, src);
            await IntegrationDb.DropTableAsync(cs, trg);
            await RelationalIngestionHarness.DropStagingAsync(cs, ingestId);
            await CleanInvoke(cs, invokeId, "PreHookAdf");
            await CleanIngestion(cs, ingestId);
            await IntegrationDb.ExecuteAsync(cs,
                "IF OBJECT_ID('flw.DataSource','U') IS NOT NULL DELETE FROM [flw].[DataSource] WHERE [Alias] = 'sf_invh_sink'; " +
                "IF OBJECT_ID('flw.CredentialProfile','U') IS NOT NULL DELETE FROM [flw].[CredentialProfile] WHERE [ProfileAlias] = 'sf_cp_invh';");
        }
    }

    private static Task CleanInvoke(string cs, int flowId, string alias)
        => IntegrationDb.ExecuteAsync(cs,
            $"IF OBJECT_ID('flw.Invoke','U') IS NOT NULL DELETE FROM [flw].[Invoke] WHERE [FlowID] = {flowId} OR [InvokeAlias] = '{alias}'; " +
            $"IF OBJECT_ID('flw.SysLog','U') IS NOT NULL DELETE FROM [flw].[SysLog] WHERE [FlowID] = {flowId}; " +
            $"IF OBJECT_ID('flw.SysStats','U') IS NOT NULL DELETE FROM [flw].[SysStats] WHERE [FlowID] = {flowId};");

    private static Task CleanIngestion(string cs, int flowId)
        => IntegrationDb.ExecuteAsync(cs,
            $"IF OBJECT_ID('flw.Ingestion','U') IS NOT NULL DELETE FROM [flw].[Ingestion] WHERE [FlowID] = {flowId}; " +
            $"IF OBJECT_ID('flw.SysLog','U') IS NOT NULL DELETE FROM [flw].[SysLog] WHERE [FlowID] = {flowId}; " +
            $"IF OBJECT_ID('flw.SysStats','U') IS NOT NULL DELETE FROM [flw].[SysStats] WHERE [FlowID] = {flowId};");
}
