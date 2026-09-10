using Microsoft.Data.SqlClient;
using SqlFlow.Catalog;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The provisioning guardrails on <see cref="CatalogDatabase.MigrateExistingAsync"/>: the automatic paths
/// (control-plane startup, per-run write-back, db sync) must refuse to conjure a database, so a wrong or mistyped
/// connection can never provision against the wrong (possibly production) server. Gated on a reachable server like
/// the other DB-backed suites.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CatalogProvisioningGuardTests
{
    [SkippableFact]
    public async Task MigrateExisting_RefusesToCreateAMissingDatabase()
    {
        var reachable = CatalogTestDb.Require();
        var missingName = $"SqlFlowGuardMissing_{Guid.NewGuid():N}";
        var missing = new SqlConnectionStringBuilder(reachable) { InitialCatalog = missingName }.ConnectionString;

        // The server is reachable but this database does not exist: refuse, do not create.
        var ex = await Assert.ThrowsAsync<CatalogProvisioningException>(
            () => CatalogDatabase.MigrateExistingAsync(missing));
        Assert.Contains("does not exist", ex.Message);
        Assert.Contains(missingName, ex.Message);
        // The message is safe to log: it names the target but never a secret.
        Assert.DoesNotContain("password", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Critically, the refusal must have no side effect: the database was NOT created.
        Assert.False(await DatabaseExistsAsync(reachable, missingName));
    }

    [SkippableFact]
    public async Task MigrateExisting_UpgradesAnExistingCatalog()
    {
        var cs = CatalogTestDb.Require();
        // Explicitly provision/upgrade the reachable test catalog (idempotent), so it exists with history.
        await CatalogDatabase.MigrateAsync(cs);
        // The guarded path then succeeds because the target is a real catalog.
        await CatalogDatabase.MigrateExistingAsync(cs);
    }

    private static async Task<bool> DatabaseExistsAsync(string serverConnectionString, string databaseName)
    {
        var master = new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = "master" }.ConnectionString;
        await using var connection = new SqlConnection(master);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DB_ID(@name)";
        command.Parameters.AddWithValue("@name", databaseName);
        var result = await command.ExecuteScalarAsync();
        return result is not null && result != DBNull.Value;
    }
}
