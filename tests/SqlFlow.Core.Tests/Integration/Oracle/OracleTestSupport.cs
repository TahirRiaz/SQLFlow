using System.Data.Common;
using SqlFlow.Core.Catalog;
using SqlFlow.Core.Connections;

namespace SqlFlow.Tests.Integration.Oracle;

/// <summary>
/// Shared fixture helpers for the Oracle provider integration tests: unique object names, best-effort drops
/// (Oracle has no DROP TABLE IF EXISTS), and the create/introspect/drop round trip the type matrix leans on.
/// Everything goes through <see cref="ForeignDb"/> so it opens and introspects on the production provider path.
/// </summary>
internal static class OracleTestSupport
{
    public const DataSourceKind Kind = DataSourceKind.Oracle;

    /// <summary>A unique, upper-case, Oracle-legal (<=30 byte) object name. Oracle folds unquoted identifiers
    /// to upper case, so the stored name is upper and callers reference it that way.</summary>
    public static string Unique(string prefix)
    {
        var raw = $"{prefix}{Guid.NewGuid():N}";
        return raw[..Math.Min(30, raw.Length)].ToUpperInvariant();
    }

    public static async Task ExecAsync(DbConnection connection, string sql, CancellationToken ct = default)
        => await ForeignDb.ExecAsync(connection, sql, ct).ConfigureAwait(false);

    /// <summary>Drops a table, swallowing ORA-00942 (does not exist), and purges it from the recycle bin.</summary>
    public static Task DropTableAsync(DbConnection connection, string table, CancellationToken ct = default)
        => ForeignDb.ExecAsync(connection,
            $"BEGIN EXECUTE IMMEDIATE 'DROP TABLE {table} PURGE'; EXCEPTION WHEN OTHERS THEN IF SQLCODE != -942 THEN RAISE; END IF; END;",
            ct);

    public static async Task<string> CurrentSchemaAsync(DbConnection connection, CancellationToken ct = default)
        => (string)(await ForeignDb.ScalarAsync(connection, "SELECT USER FROM DUAL", ct).ConfigureAwait(false))!;

    /// <summary>Introspects a table or view in the given schema through the production catalog reader.</summary>
    public static Task<CatalogObject?> IntrospectAsync(DbConnection connection, string schema, string name, CancellationToken ct = default)
        => ForeignDb.Reader(Kind).IntrospectObjectAsync(connection, new ThreePartName { Schema = schema, Name = name }, ct);

    /// <summary>Creates a one-column table of the given Oracle type, introspects it through the production
    /// catalog reader, and returns that single column. The table is dropped before returning.</summary>
    public static async Task<CatalogColumn> IntrospectColumnAsync(string connectionString, string oracleType, CancellationToken ct = default)
    {
        await using var connection = await ForeignDb.OpenAsync(Kind, connectionString, ct).ConfigureAwait(false);
        var schema = await CurrentSchemaAsync(connection, ct).ConfigureAwait(false);
        var table = Unique("SF_COL");
        await DropTableAsync(connection, table, ct).ConfigureAwait(false);
        // TABLESPACE USERS (automatic segment space management) so LOB-backed types such as XMLTYPE, which
        // require a SecureFiles LOB, can be created even when the connecting user (for example SYSTEM) defaults
        // to a non-ASSM tablespace. This is a fixture concern, not a provider one.
        await ExecAsync(connection, $"CREATE TABLE {table} (c {oracleType}) TABLESPACE USERS", ct).ConfigureAwait(false);
        try
        {
            var reader = ForeignDb.Reader(Kind);
            var name = new ThreePartName { Schema = schema, Name = table };
            var introspected = await reader.IntrospectObjectAsync(connection, name, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Introspection returned null for freshly created {table}.");
            return introspected.Columns.Single(column => string.Equals(column.Name, "C", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await DropTableAsync(connection, table, ct).ConfigureAwait(false);
        }
    }
}
