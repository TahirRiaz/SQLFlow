using Microsoft.Data.SqlClient;
using SqlFlow.Catalog.Modules;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Whether a module's rows are reachable on a host's connection, which is what decides whether the module may do its
/// work there and share the host's transaction. A module database can be a database of its own, on its own server, and
/// on Azure SQL there is no cross-database statement at all: a module that assumed otherwise would write its rows into
/// the host's database or fail outright. The comparison is conservative on purpose, because calling two connections
/// different costs a connection and calling them the same costs correctness.
/// </summary>
public sealed class ModuleDatabaseReachabilityTests
{
    private static SqlConnection Connection(string connectionString) => new(connectionString);

    [Fact]
    public void A_module_with_no_connection_of_its_own_is_wherever_the_host_points()
    {
        using var host = Connection("Server=sql1;Database=Catalog;Trusted_Connection=True");

        Assert.True(ModuleDatabase.IsReachableOn(host, null));
        Assert.True(ModuleDatabase.IsReachableOn(host, "   "));
    }

    [Theory]
    [InlineData("Server=sql1;Database=Catalog;Trusted_Connection=True")]
    [InlineData("Server=SQL1;Initial Catalog=catalog;Trusted_Connection=True")]
    [InlineData("Data Source=tcp:sql1;Initial Catalog=Catalog;Trusted_Connection=True")]
    [InlineData("Server=sql1,1433;Database=Catalog;Trusted_Connection=True")]
    public void The_same_server_and_database_is_reachable_however_it_is_spelled(string module)
    {
        using var host = Connection("Server=sql1;Database=Catalog;Trusted_Connection=True");

        Assert.True(ModuleDatabase.IsReachableOn(host, module));
    }

    [Theory]
    // Another database on the same server: one statement cannot touch both on Azure SQL.
    [InlineData("Server=sql1;Database=Osdu;Trusted_Connection=True")]
    // Another server entirely, which is the shape two Azure SQL databases usually take.
    [InlineData("Server=osdu.database.windows.net;Database=Catalog;Trusted_Connection=True")]
    [InlineData("Server=osdu.database.windows.net;Database=Osdu;Trusted_Connection=True")]
    // A connection string that names no database says nothing about where its rows are.
    [InlineData("Server=sql1;Trusted_Connection=True")]
    public void Anything_it_cannot_prove_identical_is_not_reachable(string module)
    {
        using var host = Connection("Server=sql1;Database=Catalog;Trusted_Connection=True");

        Assert.False(ModuleDatabase.IsReachableOn(host, module));
    }

    [Fact]
    public void Two_azure_databases_on_one_logical_server_are_still_two_databases()
    {
        using var host = Connection("Server=tcp:estate.database.windows.net,1433;Database=sqlflow-catalog;Authentication=Active Directory Default");

        Assert.True(ModuleDatabase.IsReachableOn(host, "Server=estate.database.windows.net;Database=sqlflow-catalog;Authentication=Active Directory Default"));
        Assert.False(ModuleDatabase.IsReachableOn(host, "Server=estate.database.windows.net;Database=sqlflow-osdu;Authentication=Active Directory Default"));
    }

    [Fact]
    public void A_connection_string_it_cannot_read_is_one_it_cannot_vouch_for()
    {
        using var host = Connection("Server=sql1;Database=Catalog;Trusted_Connection=True");

        Assert.False(ModuleDatabase.IsReachableOn(host, "this is not a connection string=;;;="));
    }

    [Fact]
    public void An_alias_for_one_server_reads_as_another_server_because_the_safe_answer_is_its_own_connection()
    {
        using var host = Connection("Server=.;Database=Catalog;Trusted_Connection=True");

        // '.' and 'localhost' are the same instance; proving that needs a resolver, so the comparison does not claim it.
        Assert.False(ModuleDatabase.IsReachableOn(host, "Server=localhost;Database=Catalog;Trusted_Connection=True"));
        Assert.True(ModuleDatabase.IsReachableOn(host, "Server=.;Database=Catalog;Trusted_Connection=True"));
    }
}
