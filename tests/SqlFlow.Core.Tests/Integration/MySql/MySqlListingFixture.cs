using SqlFlow.Core.Connections;
using Xunit;

namespace SqlFlow.Tests.Integration.MySql;

/// <summary>
/// Provisions a dedicated <c>sf_itest</c> database with a known set of tables and one view for the listing and
/// search tests, and tears it down afterwards. A dedicated database keeps the object totals deterministic
/// regardless of what the connected database already contains. Everything is best-effort: if MySQL is
/// unreachable or the login cannot create a database, <see cref="Available"/> stays false and the tests skip
/// with <see cref="SkipReason"/> instead of failing. Names are created lower case to match MySQL's typical
/// <c>lower_case_table_names=1</c> default.
/// </summary>
public sealed class MySqlListingFixture : IAsyncLifetime
{
    public const string Schema = "sf_itest";
    private const DataSourceKind Kind = DataSourceKind.MySQL;

    public bool Available { get; private set; }
    public string SkipReason { get; private set; } = "MySQL listing fixture was not initialized.";
    public string ConnectionString { get; private set; } = string.Empty;

    private bool _schemaCreated;

    public async Task InitializeAsync()
    {
        var cs = ForeignDb.ConnectionString(Kind);
        if (string.IsNullOrWhiteSpace(cs))
        {
            SkipReason = $"Set {ForeignDb.EnvVar(Kind)} to run the MySQL listing tests (see docker/README.md).";
            return;
        }

        ConnectionString = cs;

        try
        {
            await using var connection = await ForeignDb.OpenAsync(Kind, cs);

            await DropSchemaAsync(connection);
            await ForeignDb.ExecAsync(connection, $"CREATE DATABASE `{Schema}` CHARACTER SET utf8mb4");
            _schemaCreated = true;

            await ForeignDb.ExecAsync(connection, $"CREATE TABLE `{Schema}`.`sf_l_alpha` (id INT NOT NULL PRIMARY KEY, label VARCHAR(50))");
            await ForeignDb.ExecAsync(connection, $"CREATE TABLE `{Schema}`.`sf_l_beta` (id INT NOT NULL PRIMARY KEY, amount DECIMAL(10,2))");
            await ForeignDb.ExecAsync(connection, $"CREATE TABLE `{Schema}`.`sf_l_gamma` (id INT NOT NULL PRIMARY KEY, created DATE)");
            await ForeignDb.ExecAsync(connection, $"CREATE VIEW `{Schema}`.`sf_l_view` AS SELECT id, label FROM `{Schema}`.`sf_l_alpha`");

            Available = true;
        }
        catch (Exception ex)
        {
            Available = false;
            SkipReason = $"MySQL listing fixture could not provision the {Schema} database (needs CREATE DATABASE): {ex.Message}";
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
            // Best-effort teardown; the database is disposable and local.
        }
    }

    private static Task DropSchemaAsync(System.Data.Common.DbConnection connection)
        => ForeignDb.ExecAsync(connection, $"DROP DATABASE IF EXISTS `{Schema}`");
}
