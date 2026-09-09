using SqlFlow.SqlServer.FullMode;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// The headline full-mode test: seed the control plane (a secretless data-source alias, a credential profile,
/// an assertion, and a normalized flw.Ingestion flow) in the sink, then construct a FullModeIngestionHost and
/// run the flow by id. Proves the whole with-database path end to end: the flow loads through the lossless
/// mapper, the alias resolves through the SQL data-source store, the load runs on the same IngestionFlowRunner
/// as without-database mode, the run is logged to flw.SysLog, and the registry-backed assertion is evaluated.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FullModeIngestionHostIntegrationTests
{
    private const int FlowId = 99;
    private const string Src = "_SfE2E_Src";
    private const string Trg = "_SfE2E_Trg";

    [SkippableFact]
    public async Task FullMode_LoadsFlowFromControlDb_RunsEndToEnd_LogsAndAsserts()
    {
        var cs = IntegrationDb.Require();
        await ControlPlaneSchema.EnsureAsync(cs);
        await CleanUp(cs);

        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{Src}] ([Id] int NOT NULL, [Val] nvarchar(20) NULL);");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{Src}] VALUES (1,'a'),(2,'b');");

        // Seed the control plane: credential profile, secretless data-source alias, assertion, and the flow.
        await IntegrationDb.ExecuteAsync(cs, "INSERT INTO [flw].[CredentialProfile] ([ProfileAlias],[Mode]) VALUES ('sf_cp_e2e','InlineConnectionString');");
        await IntegrationDb.ExecuteAsync(cs, "INSERT INTO [flw].[DataSource] ([Alias],[Kind],[ConnectionRef],[CredentialProfileID]) VALUES ('sf_e2e_sink','MSSQL','${env:SQLFlowSinkConStr}',(SELECT [CredentialProfileID] FROM [flw].[CredentialProfile] WHERE [ProfileAlias]='sf_cp_e2e'));");
        await IntegrationDb.ExecuteAsync(cs, "INSERT INTO [flw].[Assertion] ([AssertionName],[AssertionExp]) VALUES ('SfE2ECheck','SELECT COUNT(*) FROM @TableName WHERE 1=1 @FilterCriteria');");
        await IntegrationDb.ExecuteAsync(cs,
            $"INSERT INTO [flw].[Ingestion] ([FlowID],[SysAlias],[srcServer],[srcDBSchTbl],[trgServer],[trgDBSchTbl],[KeyColumns],[Assertions]) " +
            $"VALUES ({FlowId},'e2e-flow','sf_e2e_sink','[db].[dbo].[{Src}]','sf_e2e_sink','[db].[dbo].[{Trg}]','Id','SfE2ECheck');");

        try
        {
            var host = FullModeIngestionHost.Create(new FullModeOptions { ControlConnectionString = cs });

            // The flow loads from the control DB through the lossless mapper.
            var flow = await host.Flows.LoadByIdAsync(FlowId);
            Assert.Equal("@sf_e2e_sink", flow.Source.ConnectionReference);
            Assert.Equal(new[] { "Id" }, flow.Load.KeyColumns);
            Assert.Equal(new[] { "SfE2ECheck" }, flow.Assertions);

            // Run it end to end through the host.
            var result = await host.RunFlowByIdAsync(FlowId);
            Assert.True(result.Success, result.Error);
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, Trg));

            // The run was logged to flw.SysLog (with-database mode).
            Assert.Equal(2L, await IntegrationDb.ScalarAsync<long?>(cs, $"SELECT [Fetched] FROM [flw].[SysLog] WHERE FlowID = {FlowId}"));
            Assert.Equal(1, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT CONVERT(int, [Success]) FROM [flw].[SysLog] WHERE FlowID = {FlowId}"));

            // The registry-backed assertion was evaluated.
            Assert.Contains(result.Assertions, a => a.Name == "SfE2ECheck" && a.Evaluated && a.Result == "2");
        }
        finally
        {
            await CleanUp(cs);
        }
    }

    private static async Task CleanUp(string cs)
    {
        await IntegrationDb.DropTableAsync(cs, Src);
        await IntegrationDb.DropTableAsync(cs, Trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, FlowId);
        await IntegrationDb.ExecuteAsync(cs,
            $"IF OBJECT_ID('flw.Ingestion','U') IS NOT NULL DELETE FROM [flw].[Ingestion] WHERE [FlowID] = {FlowId}; " +
            "IF OBJECT_ID('flw.DataSource','U') IS NOT NULL DELETE FROM [flw].[DataSource] WHERE [Alias] = 'sf_e2e_sink'; " +
            "IF OBJECT_ID('flw.CredentialProfile','U') IS NOT NULL DELETE FROM [flw].[CredentialProfile] WHERE [ProfileAlias] = 'sf_cp_e2e'; " +
            "IF OBJECT_ID('flw.Assertion','U') IS NOT NULL DELETE FROM [flw].[Assertion] WHERE [AssertionName] = 'SfE2ECheck'; " +
            $"IF OBJECT_ID('flw.SysLog','U') IS NOT NULL DELETE FROM [flw].[SysLog] WHERE [FlowID] = {FlowId}; " +
            $"IF OBJECT_ID('flw.SysStats','U') IS NOT NULL DELETE FROM [flw].[SysStats] WHERE [FlowID] = {FlowId};");
    }
}
