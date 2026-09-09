using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Globalization;
using SqlFlow.Core.Catalog;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Ingestion;
using SqlFlow.Providers;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Shared, gated access to the live foreign source databases (MySQL, PostgreSQL, Oracle) that the provider
/// integration suites exercise. Each engine's connection string comes from an environment variable and the
/// helpers <see cref="Require"/>/<see cref="Reachable"/> skip a test when the database is not configured or not
/// reachable, so the unit suite keeps running everywhere. The docker compose under <c>docker/</c> brings all
/// three up; the variable names and connection strings are documented in <c>docker/README.md</c>:
/// <list type="bullet">
///   <item><c>SQLFLOW_TEST_MYSQL</c></item>
///   <item><c>SQLFLOW_TEST_PG</c></item>
///   <item><c>SQLFLOW_TEST_ORACLE</c></item>
/// </list>
/// Everything runs through the production provider surface: connections open through the registered
/// <see cref="IProviderConnectionFactory"/>, introspection through the registered <see cref="ICatalogReader"/>,
/// and type translation through the registered <see cref="ISourceTypeMapper"/>. The tests therefore assert the
/// real behavior an ingestion run would see, not a reimplementation of it.
/// </summary>
internal static class ForeignDb
{
    private static readonly SourceProviderRegistry Registry = SqlFlowSourceProviders.CreateRegistry();
    private static readonly ConcurrentDictionary<DataSourceKind, bool> ReachableCache = new();

    public static string EnvVar(DataSourceKind kind) => kind switch
    {
        DataSourceKind.MySQL => "SQLFLOW_TEST_MYSQL",
        DataSourceKind.PostgreSQL => "SQLFLOW_TEST_PG",
        DataSourceKind.Oracle => "SQLFLOW_TEST_ORACLE",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a foreign source kind."),
    };

    public static string? ConnectionString(DataSourceKind kind)
        => Environment.GetEnvironmentVariable(EnvVar(kind));

    /// <summary>Returns the connection string, skipping the test when the database is not configured or is
    /// unreachable.</summary>
    public static string Require(DataSourceKind kind)
    {
        var cs = ConnectionString(kind);
        Skip.If(string.IsNullOrWhiteSpace(cs),
            $"Set {EnvVar(kind)} to a reachable {kind} connection string to run this test (see docker/README.md).");
        Skip.IfNot(Reachable(kind),
            $"{kind} is configured via {EnvVar(kind)} but not reachable; skipping. Start it with docker/up.");
        return cs!;
    }

    private static bool Reachable(DataSourceKind kind) => ReachableCache.GetOrAdd(kind, static k =>
    {
        var cs = Environment.GetEnvironmentVariable(EnvVar(k));
        if (string.IsNullOrWhiteSpace(cs))
        {
            return false;
        }

        try
        {
            using var connection = OpenSync(k, cs);
            return connection.State == ConnectionState.Open;
        }
        catch (DbException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    });

    private static DbConnection OpenSync(DataSourceKind kind, string connectionString)
    {
        var connection = Factory(kind).OpenAsync(Resolved(kind, connectionString)).GetAwaiter().GetResult();
        return connection;
    }

    /// <summary>Opens a live connection through the production provider factory.</summary>
    public static Task<DbConnection> OpenAsync(DataSourceKind kind, string connectionString, CancellationToken ct = default)
        => Factory(kind).OpenAsync(Resolved(kind, connectionString), ct);

    /// <summary>The production catalog reader for the kind.</summary>
    public static ICatalogReader Reader(DataSourceKind kind)
        => new CompositeCatalogReaderFactory(Registry.CatalogReaders).ReaderFor(Resolved(kind, "unused"));

    /// <summary>The production type mapper for the kind.</summary>
    public static ISourceTypeMapper Mapper(DataSourceKind kind)
        => Registry.TypeMappers.First(m => m.CanHandle(kind));

    /// <summary>Opens, introspects one object through the production catalog reader, and closes.</summary>
    public static async Task<CatalogObject?> IntrospectAsync(DataSourceKind kind, string connectionString, ThreePartName name, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(kind, connectionString, ct).ConfigureAwait(false);
        return await Reader(kind).IntrospectObjectAsync(connection, name, ct).ConfigureAwait(false);
    }

    private static IProviderConnectionFactory Factory(DataSourceKind kind)
        => Registry.ConnectionFactories.First(f => f.CanHandle(kind));

    private static ResolvedConnection Resolved(DataSourceKind kind, string connectionString) => new()
    {
        Kind = kind,
        CanonicalString = connectionString,
        RedactedString = connectionString,
    };

    // ---- Statement helpers over an open connection (one statement per call) ----

    public static async Task ExecAsync(DbConnection connection, string sql, CancellationToken ct = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static async Task<object?> ScalarAsync(DbConnection connection, string sql, CancellationToken ct = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is DBNull ? null : value;
    }

    public static async Task<long> Int64Async(DbConnection connection, string sql, CancellationToken ct = default)
    {
        var value = await ScalarAsync(connection, sql, ct).ConfigureAwait(false);
        return value is null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }
}
