using Microsoft.Data.SqlClient;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Secrets;
using SqlFlow.SqlServer;
using SqlFlow.SqlServer.Catalog;
using SqlFlow.SqlServer.FullMode;
using SqlFlow.SqlServer.Ingestion;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Exercises surrogate-key generation against the real sink: the local path (generate IDENTITY keys, stamp the
/// base, idempotent on re-run), the full-mode path (a flw.SurrogateKey config attached to a flw.Ingestion flow
/// stamps the loaded target), and the remote path (the surrogate table on a second database reached over a
/// separate connection with a bulk-copy round-trip, no linked server).
/// </summary>
[Trait("Category", "Integration")]
public sealed class SurrogateKeyIntegrationTests
{
    [SkippableFact]
    public async Task Local_GeneratesKeys_StampsBase_AndIsIdempotent()
    {
        var cs = IntegrationDb.Require();
        var db = await IntegrationDb.ScalarAsync<string?>(cs, "SELECT DB_NAME();");
        const string baseTbl = "_SfSk_Base";
        const string dimTbl = "_SfSk_Dim";
        await Drop(cs, baseTbl, dimTbl);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{baseTbl}] ([CustId] int NOT NULL, [Name] nvarchar(20) NULL);");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{baseTbl}] ([CustId],[Name]) VALUES (10,'x'),(20,'y'),(10,'x2');");

        try
        {
            var executor = new SurrogateKeyExecutor(RelationalIngestionHarness.BuildResolver(), new SqlServerCatalogReader());
            var flow = FlowWith(db!, baseTbl, new SurrogateKeySpec
            {
                SurrogateKeyId = 1,
                FlowId = 60,
                SurrogateTable = new RelationalObject { Database = db!, Schema = "dbo", Name = dimTbl },
                SurrogateColumn = "CustKey",
                KeyColumns = ["CustId"],
            });

            var result = Assert.Single(await executor.RunAsync(flow, cs));
            Assert.True(result.Executed, result.Error);
            Assert.False(result.IsRemote);
            Assert.Equal(2, result.KeysGenerated);
            Assert.Equal(3, result.RowsStamped);

            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, dimTbl));
            Assert.Equal(0, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{baseTbl}] WHERE [CustKey] IS NULL"));
            Assert.Equal(1, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(DISTINCT [CustKey]) FROM [dbo].[{baseTbl}] WHERE [CustId] = 10"));

            // Re-run: anti-join inserts no new keys and stamps no already-stamped rows.
            var second = Assert.Single(await executor.RunAsync(flow, cs));
            Assert.Equal(0, second.KeysGenerated);
            Assert.Equal(0, second.RowsStamped);
        }
        finally
        {
            await Drop(cs, baseTbl, dimTbl);
        }
    }

    [SkippableFact]
    public async Task FullMode_StampsLoadedTarget_FromControlConfig()
    {
        const int flowId = 61;
        var cs = IntegrationDb.Require();
        await ControlPlaneSchema.EnsureAsync(cs);
        var db = await IntegrationDb.ScalarAsync<string?>(cs, "SELECT DB_NAME();");
        const string src = "_SfSkFm_Src";
        const string trg = "_SfSkFm_Trg";
        const string dim = "_SfSkFm_Dim";
        await Drop(cs, src, trg, dim);
        await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
        await CleanControl(cs, flowId);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{src}] ([Id] int NOT NULL, [Val] nvarchar(20) NULL);");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] ([Id],[Val]) VALUES (10,'a'),(20,'b');");

        try
        {
            await IntegrationDb.ExecuteAsync(cs, "INSERT INTO [flw].[CredentialProfile] ([ProfileAlias],[Mode]) VALUES ('sf_cp_sk','InlineConnectionString');");
            await IntegrationDb.ExecuteAsync(cs, "INSERT INTO [flw].[DataSource] ([Alias],[Kind],[ConnectionRef],[CredentialProfileID]) VALUES ('sf_sk_sink','MSSQL','${env:SQLFlowSinkConStr}',(SELECT [CredentialProfileID] FROM [flw].[CredentialProfile] WHERE [ProfileAlias]='sf_cp_sk'));");
            await IntegrationDb.ExecuteAsync(cs,
                $"INSERT INTO [flw].[Ingestion] ([FlowID],[srcServer],[srcDBSchTbl],[trgServer],[trgDBSchTbl],[KeyColumns]) " +
                $"VALUES ({flowId},'sf_sk_sink','[db].[dbo].[{src}]','sf_sk_sink','[db].[dbo].[{trg}]','Id');");
            await IntegrationDb.ExecuteAsync(cs,
                $"INSERT INTO [flw].[SurrogateKey] ([FlowID],[SurrogateDbSchTbl],[SurrogateColumn],[KeyColumns]) " +
                $"VALUES ({flowId},'[{db}].[dbo].[{dim}]','CustKey','Id');");

            var host = FullModeIngestionHost.Create(new FullModeOptions { ControlConnectionString = cs });
            var result = await host.RunFlowByIdAsync(flowId);

            Assert.True(result.Success, result.Error);
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, trg));
            Assert.Equal(0, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [CustKey] IS NULL"));
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, dim));

            var skResult = Assert.Single(result.SurrogateKeys);
            Assert.True(skResult.Executed, skResult.Error);
            Assert.Equal(2, skResult.KeysGenerated);
            Assert.Equal(2, skResult.RowsStamped);
        }
        finally
        {
            await Drop(cs, src, trg, dim);
            await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
            await CleanControl(cs, flowId);
        }
    }

    [SkippableFact]
    public async Task Remote_GeneratesOnSecondDatabase_StampsTarget_NoLinkedServer()
    {
        var cs = IntegrationDb.Require();
        const string remoteDb = "SqlFlowSkRemoteTest";
        var canCreate = await TryCreateDatabase(cs, remoteDb);
        Skip.IfNot(canCreate, "Remote surrogate-key test needs CREATE DATABASE permission.");

        const string baseTbl = "_SfSkR_Base";
        const string dimTbl = "_SfSkR_Dim";
        var remoteCs = WithDatabase(cs, remoteDb);
        await Drop(cs, baseTbl);
        await IntegrationDb.ExecuteAsync(remoteCs, $"DROP TABLE IF EXISTS [dbo].[{dimTbl}];");
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{baseTbl}] ([CustId] int NOT NULL, [Name] nvarchar(20) NULL);");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{baseTbl}] ([CustId],[Name]) VALUES (5,'p'),(6,'q'),(5,'p2');");

        try
        {
            var resolver = new ConnectionResolver(new TwoAliasStore(cs, remoteCs), new SecretResolver([new EnvSecretProvider()]), [new SqlConnectionStringCanonicalizer()]);
            var executor = new SurrogateKeyExecutor(resolver, new SqlServerCatalogReader());
            var flow = FlowWith("ignored", baseTbl, new SurrogateKeySpec
            {
                SurrogateKeyId = 1,
                FlowId = 62,
                Server = "remote",
                SurrogateTable = new RelationalObject { Database = remoteDb, Schema = "dbo", Name = dimTbl },
                SurrogateColumn = "CustKey",
                KeyColumns = ["CustId"],
            });

            var result = Assert.Single(await executor.RunAsync(flow, cs));
            Assert.True(result.Executed, result.Error);
            Assert.True(result.IsRemote);
            Assert.Equal(2, result.KeysGenerated);
            Assert.Equal(3, result.RowsStamped);

            // The dimension was created and populated on the REMOTE database.
            Assert.Equal(2, await IntegrationDb.RowCountAsync(remoteCs, dimTbl));
            // The base on the target database was stamped via the bulk-copy round-trip.
            Assert.Equal(0, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{baseTbl}] WHERE [CustKey] IS NULL"));
        }
        finally
        {
            await Drop(cs, baseTbl);
            await IntegrationDb.ExecuteAsync(remoteCs, $"DROP TABLE IF EXISTS [dbo].[{dimTbl}];");
        }
    }

    private static IngestionFlow FlowWith(string db, string baseTbl, SurrogateKeySpec spec)
        => new()
        {
            FlowId = spec.FlowId,
            Source = new IngestionSource { Server = "sink", Table = new RelationalObject { Database = db, Schema = "dbo", Name = baseTbl } },
            Target = new IngestionTarget { Server = "sink", Table = new RelationalObject { Database = db, Schema = "dbo", Name = baseTbl } },
            SurrogateKeys = [spec],
        };

    private static async Task<bool> TryCreateDatabase(string cs, string db)
    {
        try
        {
            await IntegrationDb.ExecuteAsync(cs, $"IF DB_ID('{db}') IS NULL CREATE DATABASE [{db}];");
            return true;
        }
        catch (SqlException)
        {
            return false;
        }
    }

    private static string WithDatabase(string cs, string db)
        => new SqlConnectionStringBuilder(cs) { InitialCatalog = db }.ConnectionString;

    private static async Task Drop(string cs, params string[] tables)
    {
        foreach (var table in tables)
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    private static Task CleanControl(string cs, int flowId)
        => IntegrationDb.ExecuteAsync(cs,
            $"IF OBJECT_ID('flw.SurrogateKey','U') IS NOT NULL DELETE FROM [flw].[SurrogateKey] WHERE [FlowID] = {flowId}; " +
            $"IF OBJECT_ID('flw.Ingestion','U') IS NOT NULL DELETE FROM [flw].[Ingestion] WHERE [FlowID] = {flowId}; " +
            "IF OBJECT_ID('flw.DataSource','U') IS NOT NULL DELETE FROM [flw].[DataSource] WHERE [Alias] = 'sf_sk_sink'; " +
            "IF OBJECT_ID('flw.CredentialProfile','U') IS NOT NULL DELETE FROM [flw].[CredentialProfile] WHERE [ProfileAlias] = 'sf_cp_sk'; " +
            $"IF OBJECT_ID('flw.SysLog','U') IS NOT NULL DELETE FROM [flw].[SysLog] WHERE [FlowID] = {flowId}; " +
            $"IF OBJECT_ID('flw.SysStats','U') IS NOT NULL DELETE FROM [flw].[SysStats] WHERE [FlowID] = {flowId};");

    /// <summary>Maps "sink" and "remote" to two inline (trusted) connection strings for the remote test.</summary>
    private sealed class TwoAliasStore(string sinkConnectionString, string remoteConnectionString) : IDataSourceStore
    {
        public bool SupportsAliases => true;

        public Task<DataSource> ResolveAsync(string aliasName, CancellationToken ct = default)
            => Task.FromResult(new DataSource
            {
                Alias = aliasName,
                Kind = DataSourceKind.MSSQL,
                ConnectionRef = aliasName == "remote" ? remoteConnectionString : sinkConnectionString,
                Credential = new CredentialProfile { Mode = CredentialMode.InlineConnectionString },
            });
    }
}
