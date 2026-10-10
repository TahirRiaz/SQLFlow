using Microsoft.Data.SqlClient;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Gates the PowerAI proof-value tests on a reachable AdventureWorks sample, mirroring <see cref="CatalogTestDb"/>.
/// The proof values were captured from the AdventureWorksDW2022 sample that deploy/compose restores as database
/// <c>AdventureWorks</c>, so the tests that compare against them need that database and nothing else: they read,
/// inside a transaction that is always rolled back, and never write.
///
/// The connection comes from <c>SQLFLOW_ADVENTUREWORKS_DB</c>, the same variable the compose stack sets for the
/// control plane, so a developer with the stack up needs no extra configuration. When it is unset or the server
/// is unreachable the tests skip, leaving the fixture-integrity tests (which need no database) running everywhere.
/// Reachability is probed once per process, and the probe confirms the SAMPLE is present rather than merely that
/// a server answered: an empty SQL Server would otherwise fail every proof value instead of skipping.
/// </summary>
internal static class AdventureWorksDb
{
    private static readonly Lazy<string?> ResolvedConnectionString = new(() =>
        Environment.GetEnvironmentVariable("SQLFLOW_ADVENTUREWORKS_DB"));

    private static readonly Lazy<bool> Reachable = new(() =>
    {
        var cs = ResolvedConnectionString.Value;
        if (string.IsNullOrWhiteSpace(cs))
        {
            return false;
        }

        try
        {
            using var connection = new SqlConnection(cs);
            connection.Open();

            // The proof values are meaningless without the sample's own tables, so presence of the fact table is
            // what "reachable" means here.
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM sys.tables WHERE name = 'FactResellerSales' AND SCHEMA_NAME(schema_id) = 'dbo'";
            command.CommandTimeout = 15;
            return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
        }
        catch (SqlException)
        {
            return false;
        }
    });

    /// <summary>Returns the AdventureWorks connection string, skipping the test when the sample is not there.</summary>
    public static string Require()
    {
        Skip.IfNot(
            Reachable.Value,
            "The PowerAI proof-value tests need the AdventureWorksDW sample. Set SQLFLOW_ADVENTUREWORKS_DB (e.g. "
            + "Server=localhost,14333;Database=AdventureWorks;User ID=sa;Password=...;TrustServerCertificate=True), "
            + "which deploy/compose already provides once `docker compose up -d` has restored the sample.");
        return ResolvedConnectionString.Value!;
    }
}
