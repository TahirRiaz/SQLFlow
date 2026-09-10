using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Engine;
using SqlFlow.Core.Events;
using SqlFlow.Core.Secrets;
using SqlFlow.Core.State;
using SqlFlow.Sources;
using SqlFlow.SqlServer;
using SqlFlow.SqlServer.Schema;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Shared access to the physical sink database used by the integration tests. The connection string comes
/// from the canonical <c>SQLFLOW_TEST_DB</c> environment variable (the legacy <c>SQLFlowSinkConStr</c> name
/// is honored for existing setups); when neither is set or the sink is unreachable the integration tests
/// skip (so the unit suite still runs everywhere). When only the canonical name is set, the legacy name is
/// bridged into the process environment so the <c>${env:SQLFlowSinkConStr}</c> references inside test YAML
/// keep resolving. Every integration test writes to real tables and is responsible for dropping its own
/// tables up front so it always starts fresh.
/// </summary>
internal static class IntegrationDb
{
    public const string Schema = "dbo";

    private static readonly Lazy<string?> ResolvedConnectionString = new(() =>
    {
        var canonical = Environment.GetEnvironmentVariable("SQLFLOW_TEST_DB");
        var legacy = Environment.GetEnvironmentVariable("SQLFlowSinkConStr");
        if (canonical is not null && legacy is null)
        {
            Environment.SetEnvironmentVariable("SQLFlowSinkConStr", canonical);
        }

        return canonical ?? legacy;
    });

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

    /// <summary>Returns the sink connection string, skipping the test if the sink is not reachable.</summary>
    public static string Require()
    {
        Skip.IfNot(
            Reachable.Value,
            "Integration tests need a reachable sink database. Set SQLFLOW_TEST_DB (e.g. Server=localhost,1433;Database=TestDB;User ID=...;Password=...;TrustServerCertificate=True), for example via the git-ignored .sqlflow/env file.");
        return ResolvedConnectionString.Value!;
    }

    public static FlowRunner RealRunner()
    {
        var fileStores = new IFileStore[] { new LocalFileStore() };
        return new FlowRunner(
            [
                new CsvSourceReader(new LocalFileLifecycle(), fileStores),
                new XlsSourceReader(new LocalFileLifecycle(), fileStores),
                new JsonSourceReader(new LocalFileLifecycle(), fileStores),
                new XmlSourceReader(new LocalFileLifecycle(), fileStores),
                new ParquetSourceReader(new LocalFileLifecycle(), fileStores),
                new SqlFlow.DuckDb.DuckDbSourceReader(),
            ],
            new SqlServerTypeMapper(),
            new SqlServerSchemaProvider(),
            new SqlServerColumnTypeReconciler(),
            new SqlServerDdlGenerator(),
            new SqlBulkLoader(),
            new SqlServerIndexManager(),
            new SqlServerDesiredIndexManager(),
            new SqlServerIncrementalProbe(),
            new NullStateStore(),
            NullFlowEventSink.Instance,
            new SecretResolver([new EnvSecretProvider()]),
            RealInferenceService(),
            NullLogger<FlowRunner>.Instance);
    }

    public static IInferenceService RealInferenceService()
        => new InferenceService(
            new SqlServerSchemaProvider(),
            new SqlServerColumnProfiler(),
            new TypeInferencer(),
            new SqlServerInferenceValidator(),
            new SqlServerLocaleProvider(),
            new SecretResolver([new EnvSecretProvider()]));

    public static async Task ExecuteAsync(string connectionString, string sql, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    public static async Task<T?> ScalarAsync<T>(string connectionString, string sql, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? default : (T)value;
    }

    public static Task DropTableAsync(string connectionString, string table, CancellationToken ct = default)
        => ExecuteAsync(connectionString, $"DROP TABLE IF EXISTS [{Schema}].[{Escape(table)}];", ct);

    public static async Task<bool> TableExistsAsync(string connectionString, string table, CancellationToken ct = default)
        => await ScalarAsync<int?>(connectionString,
            $"SELECT 1 WHERE OBJECT_ID('[{Schema}].[{Escape(table)}]','U') IS NOT NULL", ct) == 1;

    public static Task<long> RowCountAsync(string connectionString, string table, CancellationToken ct = default)
        => ScalarAsync<long>(connectionString, $"SELECT COUNT_BIG(*) FROM [{Schema}].[{Escape(table)}]", ct);

    public static async Task<bool> ColumnExistsAsync(string connectionString, string table, string column, CancellationToken ct = default)
        => await ScalarAsync<int?>(connectionString,
            $"SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('[{Schema}].[{Escape(table)}]') AND name = @c",
            ct, ("@c", column)) == 1;

    public static Task<string?> ColumnTypeAsync(string connectionString, string table, string column, CancellationToken ct = default)
        => ScalarAsync<string?>(connectionString,
            $@"SELECT t.name + CASE WHEN t.name IN ('varchar','char','varbinary') THEN '(' + IIF(c.max_length = -1, 'max', CONVERT(varchar, c.max_length)) + ')'
                                    WHEN t.name IN ('nvarchar','nchar') THEN '(' + IIF(c.max_length = -1, 'max', CONVERT(varchar, c.max_length / 2)) + ')'
                                    ELSE '' END
               FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
               WHERE c.object_id = OBJECT_ID('[{Schema}].[{Escape(table)}]') AND c.name = @c",
            ct, ("@c", column));

    public static async Task<bool> IndexExistsAsync(string connectionString, string table, string index, CancellationToken ct = default)
        => await ScalarAsync<int?>(connectionString,
            $"SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('[{Schema}].[{Escape(table)}]') AND name = @i",
            ct, ("@i", index)) == 1;

    public static async Task<bool> IndexIsDisabledAsync(string connectionString, string table, string index, CancellationToken ct = default)
        => await ScalarAsync<int?>(connectionString,
            $"SELECT CONVERT(int, is_disabled) FROM sys.indexes WHERE object_id = OBJECT_ID('[{Schema}].[{Escape(table)}]') AND name = @i",
            ct, ("@i", index)) == 1;

    private static async Task<T?> ScalarAsync<T>(string connectionString, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync(ct);
        return result is null or DBNull ? default : (T)result;
    }

    private static string Escape(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);
}
