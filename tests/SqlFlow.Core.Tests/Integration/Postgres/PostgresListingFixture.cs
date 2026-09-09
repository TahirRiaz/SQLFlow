using SqlFlow.Core.Connections;
using Xunit;

namespace SqlFlow.Tests.Integration.Postgres;

/// <summary>
/// Provisions a dedicated <c>sf_itest</c> schema with a known set of tables and one view for the listing and
/// search tests, and tears it down afterwards. Everything is best-effort: if PostgreSQL is unreachable or the
/// login cannot create a schema, <see cref="Available"/> stays false and the tests skip with
/// <see cref="SkipReason"/> instead of failing. The objects are created unquoted so their names store in lower
/// case, which the tests assert on.
/// </summary>
public sealed class PostgresListingFixture : IAsyncLifetime
{
    public const string Schema = "sf_itest";
    private const DataSourceKind Kind = DataSourceKind.PostgreSQL;

    public bool Available { get; private set; }
    public string SkipReason { get; private set; } = "PostgreSQL listing fixture was not initialized.";
    public string ConnectionString { get; private set; } = string.Empty;

    private bool _schemaCreated;

    public async Task InitializeAsync()
    {
        var cs = ForeignDb.ConnectionString(Kind);
        if (string.IsNullOrWhiteSpace(cs))
        {
            SkipReason = $"Set {ForeignDb.EnvVar(Kind)} to run the PostgreSQL listing tests (see docker/README.md).";
            return;
        }

        ConnectionString = cs;

        try
        {
            await using var connection = await ForeignDb.OpenAsync(Kind, cs);

            await DropSchemaAsync(connection);
            await ForeignDb.ExecAsync(connection, $"CREATE SCHEMA {Schema}");
            _schemaCreated = true;

            await ForeignDb.ExecAsync(connection, $"CREATE TABLE {Schema}.sf_l_alpha (id integer PRIMARY KEY, label varchar(50))");
            await ForeignDb.ExecAsync(connection, $"CREATE TABLE {Schema}.sf_l_beta (id integer PRIMARY KEY, amount numeric(10,2))");
            await ForeignDb.ExecAsync(connection, $"CREATE TABLE {Schema}.sf_l_gamma (id integer PRIMARY KEY, created date)");
            await ForeignDb.ExecAsync(connection, $"CREATE VIEW {Schema}.sf_l_view AS SELECT id, label FROM {Schema}.sf_l_alpha");

            Available = true;
        }
        catch (Exception ex)
        {
            Available = false;
            SkipReason = $"PostgreSQL listing fixture could not provision the {Schema} schema (needs CREATE privilege): {ex.Message}";
        }
    }

    public async Task DisposeAsync()
    {
        if (!_schemaCreated)
        {
            return;
        }

        try
        {
            await using var connection = await ForeignDb.OpenAsync(Kind, ConnectionString);
            await DropSchemaAsync(connection);
        }
        catch
        {
            // Best-effort teardown; the schema is disposable and local.
        }
    }

    private static async Task DropSchemaAsync(System.Data.Common.DbConnection connection)
        => await ForeignDb.ExecAsync(connection, $"DROP SCHEMA IF EXISTS {Schema} CASCADE");
}
