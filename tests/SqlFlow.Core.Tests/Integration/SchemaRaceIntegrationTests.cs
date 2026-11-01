using Microsoft.Data.SqlClient;
using SqlFlow.SqlServer.Schema;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Flows running side by side on a fresh database all find a schema missing and create it. Before the statement tried the create
/// and looked again, every one but the first failed with "There is already an object named ...", and a first run depended on
/// timing. These run the statement from many connections at once, against a real server.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SchemaRaceIntegrationTests
{
    private const int Rounds = 12;

    private const int Connections = 16;

    private static async Task DropAsync(string connectionString, string schema)
        => await IntegrationDb.ExecuteAsync(connectionString, $"IF SCHEMA_ID(N'{schema}') IS NOT NULL DROP SCHEMA [{schema}];");

    private static async Task RaceAsync(string connectionString, Func<SqlConnection, Task> work)
    {
        using var start = new ManualResetEventSlim(false);
        var runners = Enumerable.Range(0, Connections).Select(_ => Task.Run(async () =>
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            start.Wait();
            await work(connection);
        })).ToList();
        await Task.Delay(100);
        start.Set();
        await Task.WhenAll(runners);
    }

    [SkippableFact]
    public async Task Many_connections_ensuring_one_schema_all_succeed()
    {
        var connectionString = IntegrationDb.Require();
        var schema = "race_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            for (var round = 0; round < Rounds; round++)
            {
                await DropAsync(connectionString, schema);
                await RaceAsync(connectionString, async connection =>
                {
                    await using var command = new SqlCommand(SchemaStatements.Ensure(schema), connection);
                    await command.ExecuteNonQueryAsync();
                });
                Assert.Equal(1, await IntegrationDb.ScalarAsync<int>(connectionString, $"SELECT COUNT(*) FROM sys.schemas WHERE name = N'{schema}'"));
            }
        }
        finally
        {
            await DropAsync(connectionString, schema);
        }
    }

    [SkippableFact]
    public async Task Connections_ensuring_it_inside_a_transaction_all_commit()
    {
        var connectionString = IntegrationDb.Require();
        var schema = "race_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            for (var round = 0; round < Rounds; round++)
            {
                await DropAsync(connectionString, schema);
                await RaceAsync(connectionString, async connection =>
                {
                    await using var transaction = await connection.BeginTransactionAsync();
                    await using var command = new SqlCommand(SchemaStatements.Ensure(schema), connection, (SqlTransaction)transaction);
                    await command.ExecuteNonQueryAsync();
                    await transaction.CommitAsync();
                });
                Assert.Equal(1, await IntegrationDb.ScalarAsync<int>(connectionString, $"SELECT COUNT(*) FROM sys.schemas WHERE name = N'{schema}'"));
            }
        }
        finally
        {
            await DropAsync(connectionString, schema);
        }
    }

    [SkippableFact]
    public async Task A_create_that_fails_for_another_reason_still_fails()
    {
        var connectionString = IntegrationDb.Require();

        // Names over 128 characters are refused by the server: the statement must not take every failure for a lost race.
        var exception = await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ExecuteAsync(connectionString, SchemaStatements.Ensure(new string('x', 200))));

        Assert.Contains("too long", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task A_schema_name_with_a_quote_and_a_bracket_is_created_as_written()
    {
        var connectionString = IntegrationDb.Require();
        const string Schema = "o'brien]s";
        const string Dropped = "IF SCHEMA_ID(N'o''brien]s') IS NOT NULL DROP SCHEMA [o'brien]]s];";
        try
        {
            await IntegrationDb.ExecuteAsync(connectionString, Dropped);
            await IntegrationDb.ExecuteAsync(connectionString, SchemaStatements.Ensure(Schema));
            await IntegrationDb.ExecuteAsync(connectionString, SchemaStatements.Ensure(Schema));
            Assert.Equal(1, await IntegrationDb.ScalarAsync<int>(connectionString, "SELECT COUNT(*) FROM sys.schemas WHERE name = N'o''brien]s'"));
        }
        finally
        {
            await IntegrationDb.ExecuteAsync(connectionString, Dropped);
        }
    }
}
