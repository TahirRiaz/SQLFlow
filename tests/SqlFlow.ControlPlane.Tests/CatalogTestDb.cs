using Microsoft.Data.SqlClient;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Gates the DB-backed control plane tests on a reachable catalog database, mirroring the engine suite's
/// IntegrationDb helper: the connection comes from the canonical <c>SQLFLOW_TEST_DB</c> (the legacy
/// <c>SQLFlowSinkConStr</c> name is honored), and when neither is set or the server is unreachable the test
/// skips so the always-on no-database tests still run everywhere. Reachability is probed once per process.
/// </summary>
internal static class CatalogTestDb
{
    private static readonly Lazy<string?> ResolvedConnectionString = new(() =>
        Environment.GetEnvironmentVariable("SQLFLOW_TEST_DB")
        ?? Environment.GetEnvironmentVariable("SQLFlowSinkConStr"));

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
            return true;
        }
        catch (SqlException)
        {
            return false;
        }
    });

    /// <summary>Returns the catalog connection string, skipping the test if the server is not reachable.</summary>
    public static string Require()
    {
        Skip.IfNot(
            Reachable.Value,
            "Control plane DB-backed tests need a reachable catalog database. Set SQLFLOW_TEST_DB (e.g. Server=localhost,1433;Database=TestDB;User ID=...;Password=...;TrustServerCertificate=True), for example via the git-ignored .sqlflow/env file.");
        return ResolvedConnectionString.Value!;
    }
}
