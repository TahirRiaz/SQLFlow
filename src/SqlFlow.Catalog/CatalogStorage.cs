using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Catalog;

/// <summary>One user table of the catalog's database: its schema and name, the rows it holds and the bytes it takes, its
/// indexes and the full-text and other internal structures built on it included.</summary>
public sealed record TableStorage(string Schema, string Name, long Rows, long Bytes);

/// <summary>What one column's values take: how many rows hold a value, and the bytes the values take.</summary>
public sealed record ColumnStorage(long Values, long Bytes);

/// <summary>The catalog's database as a whole: its name and the bytes its data files take on disk.</summary>
public sealed record DatabaseStorage(string Name, long DataFileBytes);

/// <summary>
/// Measures the catalog's database from SQL Server's own metadata: the rows and pages of every table, read from
/// <c>sys.partitions</c> and <c>sys.allocation_units</c> rather than counted, so a measure of a billion-row table costs what a
/// measure of an empty one does. Those views show a table to anyone who holds any permission on it, so the control plane,
/// which owns the database, sees every table and needs no server-level grant.
/// </summary>
public static partial class CatalogStorage
{
    /// <summary>The page size SQL Server stores data in.</summary>
    private const long PageBytes = 8192;

    // Every user table with the rows of its heap or clustered index and the pages of every index, LOB and row-overflow unit,
    // and of the internal tables built on it (full-text index fragments, XML and spatial indexes), each attributed to the
    // table they serve. An allocation unit of in-row or row-overflow data belongs to the partition's hobt, a LOB unit to the
    // partition itself.
    private const string TablesSql = """
        WITH [parts] AS (
            SELECT COALESCE([it].[parent_object_id], [p].[object_id]) AS [object_id],
                   CASE WHEN [it].[object_id] IS NULL AND [p].[index_id] IN (0, 1) THEN [p].[rows] ELSE 0 END AS [rows],
                   (SELECT COALESCE(SUM([a].[total_pages]), 0) FROM sys.allocation_units AS [a]
                     WHERE ([a].[type] IN (1, 3) AND [a].[container_id] = [p].[hobt_id])
                        OR ([a].[type] = 2 AND [a].[container_id] = [p].[partition_id])) AS [pages]
            FROM sys.partitions AS [p]
            LEFT JOIN sys.internal_tables AS [it] ON [it].[object_id] = [p].[object_id]
        )
        SELECT [s].[name] AS [Schema], [t].[name] AS [Name],
               CAST(SUM([parts].[rows]) AS bigint) AS [Rows], CAST(SUM([parts].[pages]) AS bigint) AS [Pages]
        FROM [parts]
        JOIN sys.tables AS [t] ON [t].[object_id] = [parts].[object_id]
        JOIN sys.schemas AS [s] ON [s].[schema_id] = [t].[schema_id]
        WHERE [t].[is_ms_shipped] = 0
        GROUP BY [s].[name], [t].[name]
        """;

    private const string DatabaseSql = """
        SELECT DB_NAME() AS [Name], CAST(COALESCE(SUM(CAST([size] AS bigint)), 0) AS bigint) AS [Pages]
        FROM sys.database_files WHERE [type] = 0
        """;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    /// <summary>Every user table of the database <paramref name="db"/> is connected to, with its rows and bytes.</summary>
    public static async Task<IReadOnlyList<TableStorage>> MeasureTablesAsync(DbContext db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await db.Database.SqlQueryRaw<TableRow>(TablesSql).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(r => new TableStorage(r.Schema, r.Name, r.Rows, r.Pages * PageBytes)).ToList();
    }

    /// <summary>The database's name and the bytes its data files take.</summary>
    public static async Task<DatabaseStorage> MeasureDatabaseAsync(DbContext db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var row = await db.Database.SqlQueryRaw<DatabaseRow>(DatabaseSql).SingleAsync(ct).ConfigureAwait(false);
        return new DatabaseStorage(row.Name, row.Pages * PageBytes);
    }

    /// <summary>
    /// How many rows of <paramref name="schema"/>.<paramref name="table"/> hold a value in <paramref name="column"/>, and the
    /// bytes those values take. Unlike the table measure this reads the table (the values' lengths are nowhere else), so it is
    /// asked only for a column an area clears. It reads uncommitted data, because it is an estimate shown to a person and must
    /// never wait on, or hold up, the writers of a busy table.
    /// </summary>
    /// <exception cref="ArgumentException">A name is not a plain SQL identifier.</exception>
    public static async Task<ColumnStorage> MeasureColumnAsync(
        DbContext db, string schema, string table, string column, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var target = $"{Quote(schema)}.{Quote(table)}";
        var value = Quote(column);
        var sql = $"SELECT COUNT_BIG({value}) AS [Values], COALESCE(SUM(CAST(DATALENGTH({value}) AS bigint)), 0) AS [Bytes] FROM {target} WITH (READUNCOMMITTED)";
        var row = await db.Database.SqlQueryRaw<ColumnRow>(sql).SingleAsync(ct).ConfigureAwait(false);
        return new ColumnStorage(row.Values, row.Bytes);
    }

    /// <summary>A plain identifier in brackets; anything else is refused, so no name reaches the statement unchecked.</summary>
    private static string Quote(string identifier)
    {
        if (string.IsNullOrEmpty(identifier) || !IdentifierPattern().IsMatch(identifier))
        {
            throw new ArgumentException($"'{identifier}' is not a plain SQL identifier.", nameof(identifier));
        }

        return $"[{identifier}]";
    }

    // The shapes the raw queries read into: plain settable classes, which is what EF Core materializes an unmapped query into.
    internal sealed class TableRow
    {
        public string Schema { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public long Rows { get; set; }

        public long Pages { get; set; }
    }

    internal sealed class DatabaseRow
    {
        public string Name { get; set; } = string.Empty;

        public long Pages { get; set; }
    }

    internal sealed class ColumnRow
    {
        public long Values { get; set; }

        public long Bytes { get; set; }
    }
}
