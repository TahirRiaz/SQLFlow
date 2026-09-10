using System.Data;
using Microsoft.Data.SqlClient;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Secrets;
using SqlFlow.SqlServer;
using SqlFlow.SqlServer.FullMode;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Exercises the V3-native full-mode control plane against the real sink: the idempotent schema and its
/// secretless CHECK, the SqlDataSourceStore resolving an alias through the real ConnectionResolver (and the
/// read-side gate catching a bypassed resting secret), and the SqlAssertionDefinitionStore resolving active
/// names case-insensitively. The control tables live in the sink (TestDB); each test owns and cleans its rows.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ControlPlaneStoresIntegrationTests
{
    [SkippableFact]
    public async Task Schema_IsIdempotent_AndSecretlessCheckRejectsRestingPassword()
    {
        var cs = IntegrationDb.Require();
        await ControlPlaneSchema.EnsureAsync(cs);
        await ControlPlaneSchema.EnsureAsync(cs);
        await CleanAliases(cs, "sf_ck_pwless", "sf_ck_ref", "sf_ck_pw");

        try
        {
            // A passwordless connection string and a whole ${...} reference are both accepted.
            await IntegrationDb.ExecuteAsync(cs, "INSERT INTO [flw].[DataSource] ([Alias],[Kind],[ConnectionRef]) VALUES ('sf_ck_pwless','MSSQL','Server=x;Authentication=Active Directory Default;Encrypt=True');");
            await IntegrationDb.ExecuteAsync(cs, "INSERT INTO [flw].[DataSource] ([Alias],[Kind],[ConnectionRef]) VALUES ('sf_ck_ref','MSSQL','${env:SQLFlowSinkConStr}');");

            // A resting password is rejected by the CHECK constraint at the INSERT trust boundary.
            var ex = await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ExecuteAsync(cs,
                "INSERT INTO [flw].[DataSource] ([Alias],[Kind],[ConnectionRef]) VALUES ('sf_ck_pw','MSSQL','Server=x;User Id=u;Password=p;');"));
            Assert.Contains("CK_DataSource_Secretless", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            await CleanAliases(cs, "sf_ck_pwless", "sf_ck_ref", "sf_ck_pw");
        }
    }

    [SkippableFact]
    public async Task DataSourceStore_ResolvesAlias_ThroughResolver_AndOpensConnection()
    {
        var cs = IntegrationDb.Require();
        await ControlPlaneSchema.EnsureAsync(cs);
        await CleanAliases(cs, "sf_ds_ok");
        await IntegrationDb.ExecuteAsync(cs, "DELETE FROM [flw].[CredentialProfile] WHERE [ProfileAlias] = 'sf_cp_inline';");

        try
        {
            await IntegrationDb.ExecuteAsync(cs, "INSERT INTO [flw].[CredentialProfile] ([ProfileAlias],[Mode]) VALUES ('sf_cp_inline','InlineConnectionString');");
            await IntegrationDb.ExecuteAsync(cs,
                "INSERT INTO [flw].[DataSource] ([Alias],[Kind],[Host],[ConnectionRef],[IsSynapse],[CredentialProfileID]) " +
                "VALUES ('sf_ds_ok','MSSQL','localhost','${env:SQLFlowSinkConStr}',0,(SELECT [CredentialProfileID] FROM [flw].[CredentialProfile] WHERE [ProfileAlias]='sf_cp_inline'));");

            var store = new SqlDataSourceStore(cs);
            Assert.True(store.SupportsAliases);

            var dataSource = await store.ResolveAsync("sf_ds_ok");
            Assert.Equal(DataSourceKind.MSSQL, dataSource.Kind);
            Assert.Equal("${env:SQLFlowSinkConStr}", dataSource.ConnectionRef);
            Assert.Equal(CredentialMode.InlineConnectionString, dataSource.Credential.Mode);
            Assert.Equal("localhost", dataSource.Host);

            // Through the real resolver: the ${env:..} reference expands and canonicalizes to an openable
            // connection whose redacted form carries no password.
            var resolver = new ConnectionResolver(store, new SecretResolver([new EnvSecretProvider()]), [new SqlConnectionStringCanonicalizer()]);
            var resolved = await resolver.ResolveAsync("@sf_ds_ok", ConnectionRole.Target);
            Assert.DoesNotContain("password", resolved.RedactedString, StringComparison.OrdinalIgnoreCase);

            await using var connection = await new SqlConnectionFactory().OpenAsync(resolved);
            Assert.Equal(ConnectionState.Open, connection.State);

            await Assert.ThrowsAsync<DataSourceNotFoundException>(() => store.ResolveAsync("sf_ds_missing"));
        }
        finally
        {
            await CleanAliases(cs, "sf_ds_ok");
            await IntegrationDb.ExecuteAsync(cs, "DELETE FROM [flw].[CredentialProfile] WHERE [ProfileAlias] = 'sf_cp_inline';");
        }
    }

    [SkippableFact]
    public async Task DataSourceStore_GateOnRead_CatchesBypassedRestingSecret()
    {
        var cs = IntegrationDb.Require();
        await ControlPlaneSchema.EnsureAsync(cs);
        await CleanAliases(cs, "sf_ds_bad");

        try
        {
            // Bypass the CHECK to simulate a row that predates the constraint or was inserted with NOCHECK.
            await IntegrationDb.ExecuteAsync(cs,
                "ALTER TABLE [flw].[DataSource] NOCHECK CONSTRAINT [CK_DataSource_Secretless]; " +
                "INSERT INTO [flw].[DataSource] ([Alias],[Kind],[ConnectionRef]) VALUES ('sf_ds_bad','MSSQL','Server=x;User Id=u;Password=p;');");

            var store = new SqlDataSourceStore(cs);
            var ex = await Assert.ThrowsAsync<SecretlessViolationException>(() => store.ResolveAsync("sf_ds_bad"));
            Assert.Equal("sf_ds_bad", ex.Alias);
        }
        finally
        {
            await CleanAliases(cs, "sf_ds_bad");
            await IntegrationDb.ExecuteAsync(cs, "ALTER TABLE [flw].[DataSource] WITH CHECK CHECK CONSTRAINT [CK_DataSource_Secretless];");
        }
    }

    [SkippableFact]
    public async Task AssertionStore_ResolvesActiveNames_CaseInsensitive_OmitsMissingAndInactive()
    {
        var cs = IntegrationDb.Require();
        await ControlPlaneSchema.EnsureAsync(cs);
        await CleanAssertions(cs);

        try
        {
            await IntegrationDb.ExecuteAsync(cs, "INSERT INTO [flw].[Assertion] ([AssertionName],[AssertionExp],[IsActive]) VALUES ('SfCheckEmpty','SELECT COUNT(*) FROM @TableName',1);");
            await IntegrationDb.ExecuteAsync(cs, "INSERT INTO [flw].[Assertion] ([AssertionName],[AssertionExp],[IsActive]) VALUES ('SfCheckFresh','SELECT MAX([d]) FROM @TableName',1);");
            await IntegrationDb.ExecuteAsync(cs, "INSERT INTO [flw].[Assertion] ([AssertionName],[AssertionExp],[IsActive]) VALUES ('SfInactive','SELECT 1',0);");

            var store = new SqlAssertionDefinitionStore(cs);
            var resolved = await store.ResolveAsync(["sfcheckempty", "SfCheckFresh", "SfInactive", "SfMissing"]);

            Assert.Equal(2, resolved.Count);
            Assert.True(resolved.ContainsKey("SfCheckEmpty"));
            Assert.True(resolved.ContainsKey("sfcheckfresh"));
            Assert.False(resolved.ContainsKey("SfInactive"));
            Assert.Equal("SELECT COUNT(*) FROM @TableName", resolved["SfCheckEmpty"].Expression);

            Assert.Empty(await store.ResolveAsync([]));
        }
        finally
        {
            await CleanAssertions(cs);
        }
    }

    private static Task CleanAliases(string cs, params string[] aliases)
    {
        var list = string.Join(",", aliases.Select(a => $"'{a}'"));
        return IntegrationDb.ExecuteAsync(cs, $"IF OBJECT_ID('flw.DataSource','U') IS NOT NULL DELETE FROM [flw].[DataSource] WHERE [Alias] IN ({list});");
    }

    private static Task CleanAssertions(string cs)
        => IntegrationDb.ExecuteAsync(cs, "IF OBJECT_ID('flw.Assertion','U') IS NOT NULL DELETE FROM [flw].[Assertion] WHERE [AssertionName] LIKE 'Sf%';");
}
