using Microsoft.Data.SqlClient;
using SqlFlow.Core.Abstractions;

namespace SqlFlow.SqlServer;

/// <summary>
/// Disables/rebuilds non-clustered indexes around a bulk load via the SQL Server catalog (no SMO).
/// Disabling keeps the index definition in place, so the load runs unindexed and a plain rebuild
/// restores each index exactly, including its included columns, filter, and options. Primary-key,
/// unique-constraint, and clustered indexes are left intact: a disabled clustered index would make
/// the whole table inaccessible, and constraint-backed indexes are not safe to disable around a load.
/// </summary>
public sealed class SqlServerIndexManager : IIndexManager
{
    public async Task<IReadOnlyList<string>> DisableNonClusteredAsync(string connectionString, string schema, string table, CancellationToken ct = default)
    {
        var qualified = Qualify(schema, table);

        const string sql = """
            SELECT i.name
            FROM sys.indexes i
            WHERE i.object_id = OBJECT_ID(@table)
              AND i.type_desc = 'NONCLUSTERED'
              AND i.is_primary_key = 0
              AND i.is_unique_constraint = 0
              AND i.is_disabled = 0
              AND i.name IS NOT NULL;
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var names = new List<string>();
        await using (var command = new SqlCommand(sql, connection) { CommandTimeout = 0 })
        {
            command.Parameters.AddWithValue("@table", qualified);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                names.Add(reader.GetString(0));
            }
        }

        foreach (var name in names)
        {
            // No timeout: disabling can wait on a Sch-M lock behind long readers on a busy table.
            var disable = $"ALTER INDEX [{Escape(name)}] ON {qualified} DISABLE;";
            await using var command = new SqlCommand(disable, connection) { CommandTimeout = 0 };
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return names;
    }

    public async Task RebuildAsync(string connectionString, string schema, string table, IReadOnlyList<string> indexNames, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(indexNames);
        if (indexNames.Count == 0)
        {
            return;
        }

        var qualified = Qualify(schema, table);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        // Only rebuild indexes that still exist on the table; a recreated table will have dropped them.
        var existing = await ExistingIndexNamesAsync(connection, qualified, ct).ConfigureAwait(false);

        foreach (var name in indexNames)
        {
            if (!existing.Contains(name))
            {
                continue;
            }

            // No timeout: a rebuild scans and re-sorts the whole table, far beyond the 30s default.
            var rebuild = $"ALTER INDEX [{Escape(name)}] ON {qualified} REBUILD;";
            await using var command = new SqlCommand(rebuild, connection) { CommandTimeout = 0 };
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task<HashSet<string>> ExistingIndexNamesAsync(SqlConnection connection, string qualified, CancellationToken ct)
    {
        const string sql = "SELECT name FROM sys.indexes WHERE object_id = OBJECT_ID(@table) AND name IS NOT NULL;";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        command.Parameters.AddWithValue("@table", qualified);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static string Qualify(string schema, string table) => $"[{Escape(schema)}].[{Escape(table)}]";

    private static string Escape(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);
}
