using SqlFlow.Core.Connections;
using Xunit;

namespace SqlFlow.Tests.Integration.Oracle;

/// <summary>
/// Provisions a dedicated, non-Oracle-maintained <c>SF_ITEST</c> schema with a known set of tables and one
/// view for the listing and search tests, and tears it down afterwards. Everything is best-effort: if Oracle
/// is unreachable or the login cannot create a user, <see cref="Available"/> stays false and the tests skip
/// with <see cref="SkipReason"/> instead of failing.
/// </summary>
public sealed class OracleListingFixture : IAsyncLifetime
{
    public const string Schema = "SF_ITEST";
    private const DataSourceKind Kind = DataSourceKind.Oracle;

    public bool Available { get; private set; }
    public string SkipReason { get; private set; } = "Oracle listing fixture was not initialized.";
    public string ConnectionString { get; private set; } = string.Empty;

    private bool _schemaCreated;

    public async Task InitializeAsync()
    {
        var cs = ForeignDb.ConnectionString(Kind);
        if (string.IsNullOrWhiteSpace(cs))
        {
            SkipReason = $"Set {ForeignDb.EnvVar(Kind)} to run the Oracle listing tests (see docker/README.md).";
            return;
        }

        ConnectionString = cs;

        try
        {
            await using var connection = await ForeignDb.OpenAsync(Kind, cs);

            await DropSchemaAsync(connection);
            await ForeignDb.ExecAsync(connection,
                $"CREATE USER {Schema} IDENTIFIED BY itest DEFAULT TABLESPACE USERS QUOTA UNLIMITED ON USERS");
            _schemaCreated = true;

            await ForeignDb.ExecAsync(connection, $"CREATE TABLE {Schema}.SF_L_ALPHA (id NUMBER PRIMARY KEY, label VARCHAR2(50))");
            await ForeignDb.ExecAsync(connection, $"CREATE TABLE {Schema}.SF_L_BETA (id NUMBER PRIMARY KEY, amount NUMBER(10,2))");
            await ForeignDb.ExecAsync(connection, $"CREATE TABLE {Schema}.SF_L_GAMMA (id NUMBER PRIMARY KEY, created DATE)");
            await ForeignDb.ExecAsync(connection, $"CREATE VIEW {Schema}.SF_L_VIEW AS SELECT id, label FROM {Schema}.SF_L_ALPHA");

            Available = true;
        }
        catch (Exception ex)
        {
            Available = false;
            SkipReason = $"Oracle listing fixture could not provision the {Schema} schema (needs CREATE USER): {ex.Message}";
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
        => await ForeignDb.ExecAsync(connection,
            $"BEGIN EXECUTE IMMEDIATE 'DROP USER {Schema} CASCADE'; EXCEPTION WHEN OTHERS THEN IF SQLCODE != -1918 THEN RAISE; END IF; END;");
}
