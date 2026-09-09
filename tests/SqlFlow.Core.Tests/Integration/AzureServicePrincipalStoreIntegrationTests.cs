using Microsoft.Data.SqlClient;
using SqlFlow.Core.Connections;
using SqlFlow.SqlServer.FullMode;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// The secretless guarantees of the flw.ServicePrincipal registry against the real control DB (no Azure needed):
/// the schema CHECK rejects a plaintext client secret at insert, the store refuses to read a row that bypassed
/// the CHECK, and a clean (reference-only) row resolves to its coordinates.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AzureServicePrincipalStoreIntegrationTests
{
    [SkippableFact]
    public async Task Check_RejectsPlaintextClientSecret()
    {
        var cs = IntegrationDb.Require();
        await ControlPlaneSchema.EnsureAsync(cs);
        await Clean(cs, "sf_sp_ck");

        try
        {
            var ex = await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ExecuteAsync(cs,
                "INSERT INTO [flw].[ServicePrincipal] ([ServicePrincipalAlias],[ClientSecretRef]) VALUES ('sf_sp_ck','literal-secret');"));
            Assert.Contains("CK_ServicePrincipal_SecretRef", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await Clean(cs, "sf_sp_ck");
        }
    }

    [SkippableFact]
    public async Task Store_GateOnRead_CatchesBypassedPlaintextSecret()
    {
        var cs = IntegrationDb.Require();
        await ControlPlaneSchema.EnsureAsync(cs);
        await Clean(cs, "sf_sp_bad");

        try
        {
            // Bypass the CHECK to plant a row with a resting secret (simulating a NOCHECK insert or a legacy row).
            await IntegrationDb.ExecuteAsync(cs, "ALTER TABLE [flw].[ServicePrincipal] NOCHECK CONSTRAINT [CK_ServicePrincipal_SecretRef];");
            await IntegrationDb.ExecuteAsync(cs,
                "INSERT INTO [flw].[ServicePrincipal] ([ServicePrincipalAlias],[ClientSecretRef]) VALUES ('sf_sp_bad','literal-secret');");

            var store = new SqlServicePrincipalStore(cs);
            var ex = await Assert.ThrowsAsync<SecretlessViolationException>(() => store.ResolveAsync("sf_sp_bad"));
            Assert.Equal("sf_sp_bad", ex.Alias);
            Assert.Equal("ClientSecretRef", ex.Column);
        }
        finally
        {
            await Clean(cs, "sf_sp_bad");
        }
    }

    [SkippableFact]
    public async Task Store_ResolvesCleanRow_ToCoordinates()
    {
        var cs = IntegrationDb.Require();
        await ControlPlaneSchema.EnsureAsync(cs);
        await Clean(cs, "sf_sp_ok");

        try
        {
            await IntegrationDb.ExecuteAsync(cs,
                "INSERT INTO [flw].[ServicePrincipal] " +
                "([ServicePrincipalAlias],[TenantId],[ClientId],[ClientSecretRef],[SubscriptionId],[ResourceGroup],[DataFactoryName],[AutomationAccountName]) " +
                "VALUES ('sf_sp_ok','tenant-1','client-1','${kv:vault/sp}','sub-1','rg-1','adf-1','aa-1');");

            var profile = await new SqlServicePrincipalStore(cs).ResolveAsync("sf_sp_ok");

            Assert.Equal("sf_sp_ok", profile.Alias);
            Assert.Equal("tenant-1", profile.TenantId);
            Assert.Equal("client-1", profile.ClientId);
            Assert.Equal("${kv:vault/sp}", profile.ClientSecretRef);
            Assert.Equal("sub-1", profile.SubscriptionId);
            Assert.Equal("rg-1", profile.ResourceGroup);
            Assert.Equal("adf-1", profile.DataFactoryName);
            Assert.Equal("aa-1", profile.AutomationAccountName);
        }
        finally
        {
            await Clean(cs, "sf_sp_ok");
        }
    }

    [SkippableFact]
    public async Task Store_UnknownAlias_Throws()
    {
        var cs = IntegrationDb.Require();
        await ControlPlaneSchema.EnsureAsync(cs);
        await Assert.ThrowsAsync<ServicePrincipalNotFoundException>(() => new SqlServicePrincipalStore(cs).ResolveAsync("sf_sp_missing_xyz"));
    }

    private static Task Clean(string cs, string alias)
        => IntegrationDb.ExecuteAsync(cs,
            "IF OBJECT_ID('flw.ServicePrincipal','U') IS NOT NULL " +
            "BEGIN " +
            $"  DELETE FROM [flw].[ServicePrincipal] WHERE [ServicePrincipalAlias] = '{alias}'; " +
            "  ALTER TABLE [flw].[ServicePrincipal] WITH CHECK CHECK CONSTRAINT [CK_ServicePrincipal_SecretRef]; " +
            "END");
}
