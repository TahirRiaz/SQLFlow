using System.Data.Common;
using SqlFlow.Core.Catalog;
using SqlFlow.Core.Connections;

namespace SqlFlow.Tests.Integration.MySql;

/// <summary>
/// Shared fixture helpers for the MySQL provider integration tests: unique object names, best-effort drops
/// (MySQL supports DROP TABLE IF EXISTS), and the create/introspect/drop round trip the type matrix leans on.
/// Everything goes through <see cref="ForeignDb"/> so it opens and introspects on the production provider path.
/// In this engine a schema IS a database, so an object's "schema" part addresses the connected MySQL database.
/// </summary>
internal static class MySqlTestSupport
{
    public const DataSourceKind Kind = DataSourceKind.MySQL;

    /// <summary>A unique, lower-case, MySQL-legal (&lt;=64 char) object name. MySQL on the common
    /// <c>lower_case_table_names=1</c> default folds table names to lower case, so the stored name is lower
    /// and callers reference it that way.</summary>
    public static string Unique(string prefix)
    {
        var raw = $"{prefix}{Guid.NewGuid():N}";
        return raw[..Math.Min(64, raw.Length)].ToLowerInvariant();
    }

    public static async Task ExecAsync(DbConnection connection, string sql, CancellationToken ct = default)
        => await ForeignDb.ExecAsync(connection, sql, ct).ConfigureAwait(false);

    /// <summary>Drops a table if it exists (MySQL supports the IF EXISTS clause directly).</summary>
    public static Task DropTableAsync(DbConnection connection, string table, CancellationToken ct = default)
        => ForeignDb.ExecAsync(connection, $"DROP TABLE IF EXISTS `{table}`", ct);

    public static async Task<string> CurrentDatabaseAsync(DbConnection connection, CancellationToken ct = default)
        => (string)(await ForeignDb.ScalarAsync(connection, "SELECT DATABASE()", ct).ConfigureAwait(false))!;

    /// <summary>Introspects a table or view in the given schema (database) through the production catalog reader.</summary>
    public static Task<CatalogObject?> IntrospectAsync(DbConnection connection, string schema, string name, CancellationToken ct = default)
        => ForeignDb.Reader(Kind).IntrospectObjectAsync(connection, new ThreePartName { Schema = schema, Name = name }, ct);

    /// <summary>Creates a one-column table of the given MySQL type, introspects it through the production
    /// catalog reader, and returns that single column. The table is dropped before returning. The schema
    /// passed to <see cref="ThreePartName"/> is the connection's current database.</summary>
    public static async Task<CatalogColumn> IntrospectColumnAsync(string connectionString, string mysqlType, CancellationToken ct = default)
    {
        await using var connection = await ForeignDb.OpenAsync(Kind, connectionString, ct).ConfigureAwait(false);
        var schema = await CurrentDatabaseAsync(connection, ct).ConfigureAwait(false);
        var table = Unique("sf_col");
        await DropTableAsync(connection, table, ct).ConfigureAwait(false);
        await ExecAsync(connection, $"CREATE TABLE `{table}` (c {mysqlType})", ct).ConfigureAwait(false);
        try
        {
            var reader = ForeignDb.Reader(Kind);
            var name = new ThreePartName { Schema = schema, Name = table };
            var introspected = await reader.IntrospectObjectAsync(connection, name, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Introspection returned null for freshly created {table}.");
            return introspected.Columns.Single(column => string.Equals(column.Name, "c", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await DropTableAsync(connection, table, ct).ConfigureAwait(false);
        }
    }
}
