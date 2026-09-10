using SqlFlow.Core.Ingestion;
using SqlFlow.Core.StoredProcedures;
using SqlFlow.SqlServer.FullMode;
using SqlFlow.SqlServer.StoredProcedures;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Exercises the stored-procedure flow against the real sink: the runner resolves the target alias and executes
/// the three-part procedure (proved by its side effect), and the full-mode host loads the flow from
/// flw.StoredProcedure and runs it with a flw.SysLog entry.
/// </summary>
[Trait("Category", "Integration")]
public sealed class StoredProcedureFlowIntegrationTests
{
    private const string Marker = "_SfSp_Marker";

    [SkippableFact]
    public async Task Runner_ExecutesProcedure_OnResolvedTarget()
    {
        var cs = IntegrationDb.Require();
        var dbName = await IntegrationDb.ScalarAsync<string?>(cs, "SELECT DB_NAME();");
        await ResetMarkerAndProc(cs);

        try
        {
            var runner = new StoredProcedureFlowRunner(RelationalIngestionHarness.BuildResolver());
            var flow = new StoredProcedureFlow
            {
                FlowId = 50,
                SysAlias = "sp",
                Server = "sink",
                Procedure = new RelationalObject { Database = dbName!, Schema = "dbo", Name = "usp_SfSpHook" },
            };

            var result = await runner.RunAsync(flow);
            Assert.True(result.Success, result.Error);
            Assert.Equal(1, await IntegrationDb.RowCountAsync(cs, Marker));
        }
        finally
        {
            await CleanProc(cs);
        }
    }

    [SkippableFact]
    public async Task FullMode_LoadsStoredProcedureFlow_RunsAndLogs()
    {
        const int flowId = 9104;
        var cs = IntegrationDb.Require();
        await ControlPlaneSchema.EnsureAsync(cs);
        var dbName = await IntegrationDb.ScalarAsync<string?>(cs, "SELECT DB_NAME();");
        await ResetMarkerAndProc(cs);
        await CleanControl(cs, flowId);

        try
        {
            await IntegrationDb.ExecuteAsync(cs, "INSERT INTO [flw].[CredentialProfile] ([ProfileAlias],[Mode]) VALUES ('sf_cp_sp','InlineConnectionString');");
            await IntegrationDb.ExecuteAsync(cs, "INSERT INTO [flw].[DataSource] ([Alias],[Kind],[ConnectionRef],[CredentialProfileID]) VALUES ('sf_sp_sink','MSSQL','${env:SQLFlowSinkConStr}',(SELECT [CredentialProfileID] FROM [flw].[CredentialProfile] WHERE [ProfileAlias]='sf_cp_sp'));");
            await IntegrationDb.ExecuteAsync(cs,
                $"INSERT INTO [flw].[StoredProcedure] ([FlowID],[SysAlias],[trgServer],[trgDBSchSP]) VALUES ({flowId},'sp-flow','sf_sp_sink','[{dbName}].[dbo].[usp_SfSpHook]');");

            var host = FullModeIngestionHost.Create(new FullModeOptions { ControlConnectionString = cs });

            var flow = await host.StoredProcedureFlows.LoadByIdAsync(flowId);
            Assert.Equal("@sf_sp_sink", flow.ConnectionReference);
            Assert.Equal("usp_SfSpHook", flow.Procedure.Name);

            var result = await host.RunStoredProcedureByIdAsync(flowId);
            Assert.True(result.Success, result.Error);
            Assert.Equal(1, await IntegrationDb.RowCountAsync(cs, Marker));

            // The sp flow logged one flw.SysLog row (FlowType 'sp', Success 1).
            Assert.Equal("sp", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [FlowType] FROM [flw].[SysLog] WHERE FlowID = {flowId}"));
            Assert.Equal(1, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT CONVERT(int, [Success]) FROM [flw].[SysLog] WHERE FlowID = {flowId}"));
        }
        finally
        {
            await CleanProc(cs);
            await CleanControl(cs, flowId);
        }
    }

    private static async Task ResetMarkerAndProc(string cs)
    {
        await IntegrationDb.DropTableAsync(cs, Marker);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{Marker}] ([At] datetime2(3) NOT NULL);");
        await IntegrationDb.ExecuteAsync(cs, $"CREATE OR ALTER PROCEDURE [dbo].[usp_SfSpHook] AS INSERT INTO [dbo].[{Marker}]([At]) VALUES (SYSUTCDATETIME());");
    }

    private static async Task CleanProc(string cs)
    {
        await IntegrationDb.ExecuteAsync(cs, "DROP PROCEDURE IF EXISTS [dbo].[usp_SfSpHook];");
        await IntegrationDb.DropTableAsync(cs, Marker);
    }

    private static Task CleanControl(string cs, int flowId)
        => IntegrationDb.ExecuteAsync(cs,
            $"IF OBJECT_ID('flw.StoredProcedure','U') IS NOT NULL DELETE FROM [flw].[StoredProcedure] WHERE [FlowID] = {flowId}; " +
            "IF OBJECT_ID('flw.DataSource','U') IS NOT NULL DELETE FROM [flw].[DataSource] WHERE [Alias] = 'sf_sp_sink'; " +
            "IF OBJECT_ID('flw.CredentialProfile','U') IS NOT NULL DELETE FROM [flw].[CredentialProfile] WHERE [ProfileAlias] = 'sf_cp_sp'; " +
            $"IF OBJECT_ID('flw.SysLog','U') IS NOT NULL DELETE FROM [flw].[SysLog] WHERE [FlowID] = {flowId}; " +
            $"IF OBJECT_ID('flw.SysStats','U') IS NOT NULL DELETE FROM [flw].[SysStats] WHERE [FlowID] = {flowId};");
}
