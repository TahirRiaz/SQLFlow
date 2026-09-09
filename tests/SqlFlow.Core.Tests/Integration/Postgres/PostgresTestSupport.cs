using System.Data.Common;
using SqlFlow.Core.Catalog;
using SqlFlow.Core.Connections;

namespace SqlFlow.Tests.Integration.Postgres;

/// <summary>
/// Shared fixture helpers for the PostgreSQL provider integration tests: unique object names, idempotent drops
/// (PostgreSQL has DROP TABLE IF EXISTS), and the create/introspect/drop round trip the type matrix leans on.
/// Everything goes through <see cref="ForeignDb"/> so it opens and introspects on the production provider path.
/// The connected database's default schema is <c>public</c>, so introspection uses schema.name against it.
/// </summary>
internal static class PostgresTestSupport
{
    public const DataSourceKind Kind = DataSourceKind.PostgreSQL;

    /// <summary>The schema a plain, unqualified CREATE TABLE lands in on the connected database.</summary>
    public const string DefaultSchema = "public";

    /// <summary>A unique, lower-case, PostgreSQL-legal (&lt;=63 byte) object name. PostgreSQL folds unquoted
    /// identifiers to lower case, so the stored name is lower and callers reference it that way.</summary>
    public static string Unique(string prefix)
    {
        var raw = $"{prefix}{Guid.NewGuid():N}";
        return raw[..Math.Min(63, raw.Length)].ToLowerInvariant();
    }

    public static async Task ExecAsync(DbConnection connection, string sql, CancellationToken ct = default)
        => await ForeignDb.ExecAsync(connection, sql, ct).ConfigureAwait(false);

    /// <summary>Drops a table in the public schema if it exists; PostgreSQL supports DROP TABLE IF EXISTS
    /// directly, so no exception dance is required.</summary>
    public static Task DropTableAsync(DbConnection connection, string table, CancellationToken ct = default)
        => ForeignDb.ExecAsync(connection, $"DROP TABLE IF EXISTS public.\"{table}\"", ct);

    /// <summary>Introspects a table or view in the given schema through the production catalog reader.</summary>
    public static Task<CatalogObject?> IntrospectAsync(DbConnection connection, string schema, string name, CancellationToken ct = default)
        => ForeignDb.Reader(Kind).IntrospectObjectAsync(connection, new ThreePartName { Schema = schema, Name = name }, ct);

    /// <summary>Creates a one-column table of the given PostgreSQL type in the public schema, introspects it
    /// through the production catalog reader, and returns that single column. The column is created unquoted as
    /// <c>c</c>, so it stores lower-case, and the table is dropped before returning.</summary>
    public static async Task<CatalogColumn> IntrospectColumnAsync(string connectionString, string pgType, CancellationToken ct = default)
    {
        await using var connection = await ForeignDb.OpenAsync(Kind, connectionString, ct).ConfigureAwait(false);
        var table = Unique("sf_col");
        await DropTableAsync(connection, table, ct).ConfigureAwait(false);
        await ExecAsync(connection, $"CREATE TABLE public.{table} (c {pgType})", ct).ConfigureAwait(false);
        try
        {
            var reader = ForeignDb.Reader(Kind);
            var name = new ThreePartName { Schema = DefaultSchema, Name = table };
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
