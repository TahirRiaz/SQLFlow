using Oracle.ManagedDataAccess.Client;
using SqlFlow.Core.Ingestion;
using SqlFlow.Providers;
using SqlFlow.SqlServer.Ingestion;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// End-to-end ingestion from NON-SQL-Server sources into the SQL Server sink, authored as YAML, through the
/// single without-database code path. Each test needs a live source database and skips unless its environment
/// variable is set: SQLFLOW_TEST_MYSQL (a MySQL connection string with a writable test database),
/// SQLFLOW_TEST_PG (a PostgreSQL connection string with a writable schema), and SQLFLOW_TEST_ORACLE (an Oracle
/// connection string with a writable schema), plus the usual sink. The docker compose in <c>docker/</c> brings
/// up all three databases with the connection strings listed in <c>docker/README.md</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ForeignSourceIngestionTests
{
    private static readonly YamlIngestionFlowLoader Loader = new();

    private static async Task<IngestionRunResult> RunAsync(string yaml)
    {
        var document = Loader.Parse(yaml);
        var runner = WithoutDatabaseIngestion.BuildRunner(
            document.Connections, document.AssertionDefinitions, providers: SqlFlowSourceProviders.CreateRegistry());
        return await runner.RunAsync(document.Flow, new IngestionRunOptions { ExecMode = "test" });
    }

    [SkippableFact]
    public async Task MySqlSource_IngestsAndUpserts_IntoSqlServer()
    {
        var sink = IntegrationDb.Require();
        var mysql = Environment.GetEnvironmentVariable("SQLFLOW_TEST_MYSQL");
        Skip.If(string.IsNullOrWhiteSpace(mysql), "Set SQLFLOW_TEST_MYSQL (a MySQL connection string with a writable test database) to run this test.");

        const string trg = "_SfMy_Trg";
        await IntegrationDb.DropTableAsync(sink, trg);

        // Seed the MySQL source: typed columns covering ints, unsigned, decimal, datetime, text.
        await using (var connection = new MySqlConnector.MySqlConnection(mysql))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE IF EXISTS sf_orders;
                CREATE TABLE sf_orders (
                    id INT NOT NULL PRIMARY KEY,
                    qty INT UNSIGNED NOT NULL,
                    amount DECIMAL(10,2) NOT NULL,
                    note TEXT NULL,
                    updated_at DATETIME(3) NOT NULL
                );
                INSERT INTO sf_orders VALUES
                    (1, 5, 10.50, 'first', '2024-01-01 10:00:00.000'),
                    (2, 4294967295, 20.25, NULL, '2024-01-02 11:00:00.000');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var databaseName = new MySqlConnector.MySqlConnectionStringBuilder(mysql).Database;

        try
        {
            var yaml = $$"""
                flowType: ing
                name: mysql-e2e
                connections:
                  shop:
                    provider: mysql
                    connection: ${env:SQLFLOW_TEST_MYSQL}
                  sink: ${env:SQLFlowSinkConStr}
                source:
                  server: shop
                  object: {{databaseName}}.sf_orders
                target:
                  server: sink
                  object: db.dbo.{{trg}}
                load:
                  keyColumns: [id]
                """;

            var first = await RunAsync(yaml);
            Assert.True(first.Success, first.Error);
            Assert.Equal(2, first.RowsStaged);
            Assert.Equal(2, await IntegrationDb.RowCountAsync(sink, trg));

            // The unsigned int mapped to bigint and survived the round trip.
            Assert.Equal(4294967295L, await IntegrationDb.ScalarAsync<long?>(sink, $"SELECT [qty] FROM [dbo].[{trg}] WHERE [id] = 2"));

            // Second run: change one row, add one; the keyed upsert reconciles without duplicating.
            await using (var connection = new MySqlConnector.MySqlConnection(mysql))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE sf_orders SET amount = 99.99 WHERE id = 1;
                    INSERT INTO sf_orders VALUES (3, 7, 30.00, 'third', '2024-01-03 12:00:00.000');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var second = await RunAsync(yaml);
            Assert.True(second.Success, second.Error);
            Assert.Equal(1, second.RowsUpdated);
            Assert.Equal(1, second.RowsInserted);
            Assert.Equal(3, await IntegrationDb.RowCountAsync(sink, trg));
            Assert.Equal(99.99m, await IntegrationDb.ScalarAsync<decimal?>(sink, $"SELECT [amount] FROM [dbo].[{trg}] WHERE [id] = 1"));

            // The trace captured backtick-quoted MySQL source SQL.
            Assert.Contains(second.SqlTrace, e => e.Step == "source.select" && e.Sql.Contains("`sf_orders`", StringComparison.Ordinal));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(sink, trg);
            await CleanupMySqlAsync(mysql!);
        }
    }

    [SkippableFact]
    public async Task PostgresSource_IngestsAndUpserts_IntoSqlServer()
    {
        var sink = IntegrationDb.Require();
        var pg = Environment.GetEnvironmentVariable("SQLFLOW_TEST_PG");
        Skip.If(string.IsNullOrWhiteSpace(pg), "Set SQLFLOW_TEST_PG (a PostgreSQL connection string with a writable schema) to run this test.");

        const string trg = "_SfPg_Trg";
        await IntegrationDb.DropTableAsync(sink, trg);

        await using (var connection = new Npgsql.NpgsqlConnection(pg))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE IF EXISTS public.sf_orders;
                CREATE TABLE public.sf_orders (
                    id INT NOT NULL PRIMARY KEY,
                    amount NUMERIC(10,2) NOT NULL,
                    ref UUID NOT NULL,
                    note TEXT NULL,
                    updated_at TIMESTAMPTZ(6) NOT NULL
                );
                INSERT INTO public.sf_orders VALUES
                    (1, 10.50, 'a7be9d57-1111-2222-3333-444444444444', 'first', '2024-01-01T10:00:00Z'),
                    (2, 20.25, 'b7be9d57-1111-2222-3333-444444444444', NULL, '2024-01-02T11:00:00Z');
                """;
            await command.ExecuteNonQueryAsync();
        }

        try
        {
            var yaml = $$"""
                flowType: ing
                name: postgres-e2e
                connections:
                  erp:
                    provider: postgres
                    connection: ${env:SQLFLOW_TEST_PG}
                  sink: ${env:SQLFlowSinkConStr}
                source:
                  server: erp
                  object: db.public.sf_orders
                target:
                  server: sink
                  object: db.dbo.{{trg}}
                load:
                  keyColumns: [id]
                """;

            var first = await RunAsync(yaml);
            Assert.True(first.Success, first.Error);
            Assert.Equal(2, first.RowsStaged);
            Assert.Equal(2, await IntegrationDb.RowCountAsync(sink, trg));

            // uuid mapped to uniqueidentifier and survived.
            Assert.Equal(Guid.Parse("a7be9d57-1111-2222-3333-444444444444"),
                await IntegrationDb.ScalarAsync<Guid?>(sink, $"SELECT [ref] FROM [dbo].[{trg}] WHERE [id] = 1"));

            await using (var connection = new Npgsql.NpgsqlConnection(pg))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE public.sf_orders SET amount = 99.99 WHERE id = 1;";
                await command.ExecuteNonQueryAsync();
            }

            var second = await RunAsync(yaml);
            Assert.True(second.Success, second.Error);
            Assert.Equal(1, second.RowsUpdated);
            Assert.Equal(0, second.RowsInserted);
            Assert.Contains(second.SqlTrace, e => e.Step == "source.select" && e.Sql.Contains("\"sf_orders\"", StringComparison.Ordinal));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(sink, trg);
            await CleanupPostgresAsync(pg!);
        }
    }

    [SkippableFact]
    public async Task OracleSource_IngestsAndUpserts_IntoSqlServer()
    {
        var sink = IntegrationDb.Require();
        var oracle = Environment.GetEnvironmentVariable("SQLFLOW_TEST_ORACLE");
        Skip.If(string.IsNullOrWhiteSpace(oracle), "Set SQLFLOW_TEST_ORACLE (an Oracle connection string with a writable schema) to run this test.");

        const string trg = "_SfOra_Trg";
        await IntegrationDb.DropTableAsync(sink, trg);

        // Seed the Oracle source. Oracle runs one statement per command (no batch), and unquoted identifiers
        // fold to upper case, so the object reference below is SF_ORDERS in the connecting user's schema.
        string owner;
        await using (var connection = new OracleConnection(oracle))
        {
            await connection.OpenAsync();
            owner = (string)(await ScalarOracleAsync(connection, "SELECT USER FROM DUAL"))!;
            await DropOracleTableAsync(connection);
            await ExecuteOracleAsync(connection, """
                CREATE TABLE sf_orders (
                    id NUMBER(9) NOT NULL PRIMARY KEY,
                    amount NUMBER(10,2) NOT NULL,
                    note VARCHAR2(200) NULL,
                    updated_at TIMESTAMP(3) NOT NULL
                )
                """);
            await ExecuteOracleAsync(connection, "INSERT INTO sf_orders VALUES (1, 10.50, 'first', TIMESTAMP '2024-01-01 10:00:00.000')");
            await ExecuteOracleAsync(connection, "INSERT INTO sf_orders VALUES (2, 20.25, NULL, TIMESTAMP '2024-01-02 11:00:00.000')");
        }

        try
        {
            var yaml = $$"""
                flowType: ing
                name: oracle-e2e
                connections:
                  erp:
                    provider: oracle
                    connection: ${env:SQLFLOW_TEST_ORACLE}
                  sink: ${env:SQLFlowSinkConStr}
                source:
                  server: erp
                  object: db.{{owner}}.SF_ORDERS
                target:
                  server: sink
                  object: db.dbo.{{trg}}
                load:
                  keyColumns: [ID]
                """;

            var first = await RunAsync(yaml);
            Assert.True(first.Success, first.Error);
            Assert.Equal(2, first.RowsStaged);
            Assert.Equal(2, await IntegrationDb.RowCountAsync(sink, trg));

            // NUMBER(10,2) mapped to decimal(10, 2) and survived the round trip.
            Assert.Equal(10.50m, await IntegrationDb.ScalarAsync<decimal?>(sink, $"SELECT [amount] FROM [dbo].[{trg}] WHERE [id] = 1"));

            // Second run: change one row, add one; the keyed upsert reconciles without duplicating.
            await using (var connection = new OracleConnection(oracle))
            {
                await connection.OpenAsync();
                await ExecuteOracleAsync(connection, "UPDATE sf_orders SET amount = 99.99 WHERE id = 1");
                await ExecuteOracleAsync(connection, "INSERT INTO sf_orders VALUES (3, 30.00, 'third', TIMESTAMP '2024-01-03 12:00:00.000')");
            }

            var second = await RunAsync(yaml);
            Assert.True(second.Success, second.Error);
            Assert.Equal(1, second.RowsUpdated);
            Assert.Equal(1, second.RowsInserted);
            Assert.Equal(3, await IntegrationDb.RowCountAsync(sink, trg));
            Assert.Equal(99.99m, await IntegrationDb.ScalarAsync<decimal?>(sink, $"SELECT [amount] FROM [dbo].[{trg}] WHERE [id] = 1"));

            // The trace captured double-quoted Oracle source SQL.
            Assert.Contains(second.SqlTrace, e => e.Step == "source.select" && e.Sql.Contains("\"SF_ORDERS\"", StringComparison.Ordinal));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(sink, trg);
            await CleanupOracleAsync(oracle!);
        }
    }

    private static async Task<object?> ScalarOracleAsync(OracleConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteOracleAsync(OracleConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    // Oracle has no DROP TABLE IF EXISTS; the PL/SQL block swallows ORA-00942 (table does not exist).
    private static Task DropOracleTableAsync(OracleConnection connection)
        => ExecuteOracleAsync(connection,
            "BEGIN EXECUTE IMMEDIATE 'DROP TABLE sf_orders'; EXCEPTION WHEN OTHERS THEN IF SQLCODE != -942 THEN RAISE; END IF; END;");

    private static async Task CleanupOracleAsync(string connectionString)
    {
        try
        {
            await using var connection = new OracleConnection(connectionString);
            await connection.OpenAsync();
            await DropOracleTableAsync(connection);
        }
        catch (OracleException)
        {
            // Best-effort source cleanup.
        }
    }

    private static async Task CleanupMySqlAsync(string connectionString)
    {
        try
        {
            await using var connection = new MySqlConnector.MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE IF EXISTS sf_orders;";
            await command.ExecuteNonQueryAsync();
        }
        catch (MySqlConnector.MySqlException)
        {
            // Best-effort source cleanup.
        }
    }

    private static async Task CleanupPostgresAsync(string connectionString)
    {
        try
        {
            await using var connection = new Npgsql.NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE IF EXISTS public.sf_orders;";
            await command.ExecuteNonQueryAsync();
        }
        catch (Npgsql.NpgsqlException)
        {
            // Best-effort source cleanup.
        }
    }
}
