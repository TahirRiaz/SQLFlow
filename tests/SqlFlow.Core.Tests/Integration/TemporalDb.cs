using Microsoft.Data.SqlClient;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Sink helpers for the system-versioned temporal tests. These exist because a versioned table does not
/// behave like an ordinary one under test teardown: <c>DROP TABLE</c> fails outright on a system-versioned
/// table ("not a supported operation on system-versioned temporal tables"), so
/// <see cref="IntegrationDb.DropTableAsync"/> would leave every temporal test's table behind and poison the
/// next run. <see cref="DropVersionedTableAsync"/> is the safe teardown: unlink versioning first, then drop
/// both halves. The read helpers assert against <c>sys</c> directly rather than through the engine's own
/// introspection, so a test cannot pass because the reader and the writer share a bug.
/// </summary>
internal static class TemporalDb
{
    /// <summary>The history schema the engine defaults to (legacy flw.SysCFG Schema06Version).</summary>
    public const string HistorySchema = "ver";

    /// <summary>
    /// Drops a table that may be system-versioned, along with its history table. Unlinks versioning first
    /// (the only way SQL Server permits the drop), then drops the current table and the history table under
    /// both the default history schema and dbo, so a test that overrode the history schema still cleans up.
    /// Safe to call when the table does not exist.
    /// </summary>
    public static async Task DropVersionedTableAsync(
        string connectionString,
        string table,
        string historySchema = HistorySchema,
        string? historyTable = null,
        CancellationToken ct = default)
    {
        var history = historyTable ?? table;
        var sql = $"""
            IF OBJECT_ID('[dbo].[{Escape(table)}]', 'U') IS NOT NULL
               AND (SELECT temporal_type FROM sys.tables WHERE object_id = OBJECT_ID('[dbo].[{Escape(table)}]')) = 2
                ALTER TABLE [dbo].[{Escape(table)}] SET (SYSTEM_VERSIONING = OFF);
            DROP TABLE IF EXISTS [dbo].[{Escape(table)}];
            DROP TABLE IF EXISTS [{Escape(historySchema)}].[{Escape(history)}];
            DROP TABLE IF EXISTS [dbo].[{Escape(history)}];
            """;
        await IntegrationDb.ExecuteAsync(connectionString, sql, ct);
    }

    /// <summary>True when the table is a SYSTEM_VERSIONED_TEMPORAL_TABLE right now.</summary>
    public static async Task<bool> IsVersionedAsync(string connectionString, string table, CancellationToken ct = default)
        => await ScalarAsync<int?>(connectionString,
            $"SELECT CONVERT(int, temporal_type) FROM sys.tables WHERE object_id = OBJECT_ID('[dbo].[{Escape(table)}]')", ct) == 2;

    /// <summary>The two-part name of the linked history table, or null when the table is not versioned.</summary>
    public static Task<string?> HistoryNameAsync(string connectionString, string table, CancellationToken ct = default)
        => ScalarAsync<string?>(connectionString,
            $"""
             SELECT SCHEMA_NAME(h.schema_id) + '.' + h.[name]
             FROM sys.tables AS t JOIN sys.tables AS h ON h.object_id = t.history_table_id
             WHERE t.object_id = OBJECT_ID('[dbo].[{Escape(table)}]')
             """, ct);

    /// <summary>True when the table carries a SYSTEM_TIME period, which outlives SET (SYSTEM_VERSIONING = OFF).</summary>
    public static async Task<bool> HasPeriodAsync(string connectionString, string table, CancellationToken ct = default)
        => await ScalarAsync<int?>(connectionString,
            $"SELECT 1 FROM sys.periods WHERE object_id = OBJECT_ID('[dbo].[{Escape(table)}]') AND period_type = 1", ct) == 1;

    /// <summary>The period columns as "name:type:hidden|visible", ROW START first, for exact shape assertions.</summary>
    public static async Task<IReadOnlyList<string>> PeriodColumnsAsync(string connectionString, string table, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT c.[name] + ':' + t.[name] + '(' + CONVERT(varchar(2), c.scale) + ')'
                     + ':' + IIF(c.is_hidden = 1, 'hidden', 'visible') AS Descriptor
            FROM sys.columns AS c
            JOIN sys.types AS t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID('[dbo].[{Escape(table)}]') AND c.generated_always_type <> 0
            ORDER BY c.generated_always_type;
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var descriptors = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            descriptors.Add(reader.GetString(0));
        }

        return descriptors;
    }

    /// <summary>Rows currently in the history table (the superseded versions).</summary>
    public static Task<long> HistoryRowCountAsync(
        string connectionString,
        string table,
        string historySchema = HistorySchema,
        CancellationToken ct = default)
        => IntegrationDb.ScalarAsync<long>(connectionString,
            $"SELECT COUNT_BIG(*) FROM [{Escape(historySchema)}].[{Escape(table)}]", ct);

    /// <summary>Every version of every row, current and historical, through FOR SYSTEM_TIME ALL.</summary>
    public static Task<long> AllVersionsCountAsync(string connectionString, string table, CancellationToken ct = default)
        => IntegrationDb.ScalarAsync<long>(connectionString,
            $"SELECT COUNT_BIG(*) FROM [dbo].[{Escape(table)}] FOR SYSTEM_TIME ALL", ct);

    /// <summary>The configured HISTORY_RETENTION_PERIOD as "n UNIT", or null when the engine has no such column.</summary>
    public static Task<string?> RetentionAsync(string connectionString, string table, CancellationToken ct = default)
        => ScalarAsync<string?>(connectionString,
            $"""
             DECLARE @sql nvarchar(max) =
                 CASE WHEN COL_LENGTH('sys.tables', 'history_retention_period') IS NULL
                      THEN N'SELECT CONVERT(nvarchar(40), NULL);'
                      ELSE N'SELECT CONVERT(nvarchar(40), history_retention_period) + '' '' + history_retention_period_unit_desc
                             FROM sys.tables WHERE object_id = OBJECT_ID(''[dbo].[{Escape(table)}]'');' END;
             EXEC sp_executesql @sql;
             """, ct);

    /// <summary>Turns versioning off without dropping anything, to set up the "period survives" resume path.</summary>
    public static Task DisableVersioningAsync(string connectionString, string table, CancellationToken ct = default)
        => IntegrationDb.ExecuteAsync(connectionString,
            $"ALTER TABLE [dbo].[{Escape(table)}] SET (SYSTEM_VERSIONING = OFF);", ct);

    private static async Task<T?> ScalarAsync<T>(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? default : (T)value;
    }

    private static string Escape(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);
}
