using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Core.Catalog;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Ingestion;

using SqlFlow.Core.Secrets;

namespace SqlFlow.SqlServer.Ingestion;

/// <summary>
/// Generates surrogate keys and writes them back, post-load and post-commit, porting the legacy
/// CommonDB.BuildSKeyGenPushCmd / BuildSKeyPushCmd. The surrogate is a SQL Server IDENTITY column on a lookup
/// table; assignment is get-or-create-by-key: insert the distinct business keys not yet present (the IDENTITY
/// issues the surrogate), then stamp the assigned surrogate back onto the base/target rows that have none. Both
/// statements are idempotent on re-run (anti-join on the key, NULL-only push-back). The legacy concurrency hole
/// (no transaction, no unique key) is closed: the generate runs in a transaction with HOLDLOCK/UPDLOCK and the
/// lookup table gets a UNIQUE constraint on the business key. The surrogate server resolves through the same
/// connection registry as everything else; LOCAL (same connection as the target) runs one cross-database batch,
/// REMOTE (a different resolved connection, no linked server) round-trips the keys through temp tables with two
/// bulk copies. Log-only: a per-spec failure is captured, never thrown, so a committed load is never rolled back.
/// </summary>
public sealed class SurrogateKeyExecutor : ISurrogateKeyExecutor
{
    private readonly IConnectionResolver _resolver;
    private readonly ICatalogReader _catalog;

    public SurrogateKeyExecutor(IConnectionResolver resolver, ICatalogReader catalog)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(catalog);
        _resolver = resolver;
        _catalog = catalog;
    }

    public async Task<IReadOnlyList<SurrogateKeyResult>> RunAsync(IngestionFlow flow, string targetConnectionString, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);

        if (flow.SurrogateKeys.Count == 0)
        {
            return [];
        }

        var baseTypes = await IntrospectBaseTypesAsync(flow.Target.Table, targetConnectionString, ct).ConfigureAwait(false);
        var results = new List<SurrogateKeyResult>();
        foreach (var spec in flow.SurrogateKeys)
        {
            var stopwatch = Stopwatch.StartNew();
            var statements = new List<string>();
            try
            {
                var result = await RunOneAsync(flow, spec, targetConnectionString, baseTypes, statements, ct).ConfigureAwait(false);
                results.Add(result with { Duration = stopwatch.Elapsed, Statements = statements });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(new SurrogateKeyResult
                {
                    SurrogateKeyId = spec.SurrogateKeyId,
                    SurrogateTable = spec.SurrogateTable.QualifiedName,
                    SurrogateColumn = spec.SurrogateColumn,
                    Executed = false,
                    Error = SecretHygiene.RedactedMessage(ex),
                    Duration = stopwatch.Elapsed,
                    Statements = statements,
                });
            }
        }

        return results;
    }

    private async Task<SurrogateKeyResult> RunOneAsync(
        IngestionFlow flow, SurrogateKeySpec spec, string targetConnectionString, IReadOnlyDictionary<string, string> baseTypes,
        List<string> statements, CancellationToken ct)
    {
        if (spec.SKeyColumns.Count != 0 && spec.SKeyColumns.Count != spec.KeyColumns.Count)
        {
            throw new SqlFlowException(
                $"Surrogate key {spec.SurrogateKeyId} for {spec.SurrogateTable.QualifiedName}: sKeyColumns has {spec.SKeyColumns.Count} columns but KeyColumns has {spec.KeyColumns.Count}; they must match (positional pairing).");
        }

        var skeyColumns = spec.SKeyColumns.Count > 0 ? spec.SKeyColumns : spec.KeyColumns;
        var (skeyConnectionString, isRemote) = await ResolveSurrogateConnectionAsync(spec, targetConnectionString, ct).ConfigureAwait(false);

        // Provision: the lookup table on the surrogate connection, the surrogate column + index on the base.
        var ensureTableSql = BuildEnsureSurrogateTableSql(spec, skeyColumns, baseTypes);
        statements.Add(ensureTableSql);
        await ExecuteAsync(skeyConnectionString, ensureTableSql, ct).ConfigureAwait(false);
        var ensureColumnSql = BuildEnsureBaseColumnSql(flow.Target.Table, spec.SurrogateColumn);
        statements.Add(ensureColumnSql);
        await ExecuteAsync(targetConnectionString, ensureColumnSql, ct).ConfigureAwait(false);

        if (Gate(spec.PreProcess))
        {
            statements.Add(spec.PreProcess!.Trim());
            await ExecuteAsync(skeyConnectionString, spec.PreProcess!.Trim(), ct).ConfigureAwait(false);
        }

        var (keysGenerated, rowsStamped) = isRemote
            ? await RunRemoteAsync(flow, spec, skeyColumns, baseTypes, targetConnectionString, skeyConnectionString, statements, ct).ConfigureAwait(false)
            : await RunLocalAsync(flow, spec, skeyColumns, targetConnectionString, statements, ct).ConfigureAwait(false);

        if (Gate(spec.PostProcess))
        {
            statements.Add(spec.PostProcess!.Trim());
            await ExecuteAsync(skeyConnectionString, spec.PostProcess!.Trim(), ct).ConfigureAwait(false);
        }

        return new SurrogateKeyResult
        {
            SurrogateKeyId = spec.SurrogateKeyId,
            SurrogateTable = spec.SurrogateTable.QualifiedName,
            SurrogateColumn = spec.SurrogateColumn,
            IsRemote = isRemote,
            KeysGenerated = keysGenerated,
            RowsStamped = rowsStamped,
            Executed = true,
        };
    }

    // LOCAL: the lookup table is reachable from the target connection (same server, addressed three-part), so
    // the generate (INSERT) and push-back (UPDATE) run as two statements in one transaction.
    private static async Task<(long KeysGenerated, long RowsStamped)> RunLocalAsync(
        IngestionFlow flow, SurrogateKeySpec spec, IReadOnlyList<string> skeyColumns, string targetConnectionString,
        List<string> statements, CancellationToken ct)
    {
        var baseRef = TwoPart(flow.Target.Table);
        var skeyRef = ThreePart(spec.SurrogateTable);
        var generateSql = BuildGenerateSql(baseRef, skeyRef, spec, skeyColumns, flow.Target.Table.QualifiedName);
        var pushBackSql = BuildPushBackSql(baseRef, skeyRef, spec, skeyColumns);
        statements.Add(generateSql);
        statements.Add(pushBackSql);

        await using var connection = new SqlConnection(targetConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            long keys;
            long stamped;
            await using (var command = new SqlCommand(generateSql, connection, transaction) { CommandTimeout = 0 })
            {
                keys = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var command = new SqlCommand(pushBackSql, connection, transaction) { CommandTimeout = 0 })
            {
                stamped = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return (keys, stamped);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    // REMOTE: the lookup table is on another server (no linked server), so the keys round-trip through temp
    // tables. Copy distinct base keys to a temp on the surrogate server, generate + stamp the temp there, copy
    // the stamped temp back to a temp on the target server, then push it onto the base. Temps are run-scoped
    // and always dropped.
    private static async Task<(long KeysGenerated, long RowsStamped)> RunRemoteAsync(
        IngestionFlow flow, SurrogateKeySpec spec, IReadOnlyList<string> skeyColumns, IReadOnlyDictionary<string, string> baseTypes,
        string targetConnectionString, string skeyConnectionString, List<string> statements, CancellationToken ct)
    {
        var runToken = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
        var tempName = $"_SfSkTmp_{flow.FlowId}_{runToken}";
        var tempRef = $"[dbo].[{Escape(tempName)}]";
        var baseRef = TwoPart(flow.Target.Table);
        var skeyRef = ThreePart(spec.SurrogateTable);

        var tempDdl = string.Join(", ", spec.KeyColumns.Select(k => $"[{Escape(k)}] {TypeOf(baseTypes, k)} NULL"))
            + $", [{Escape(spec.SurrogateColumn)}] int NULL";
        var keyColumnsList = string.Join(", ", spec.KeyColumns.Select(c => $"[{Escape(c)}]"));
        var copyColumns = spec.KeyColumns.ToList();
        var stampedColumns = new List<string>(spec.KeyColumns) { spec.SurrogateColumn };

        long keysGenerated = 0;
        long rowsStamped = 0;
        try
        {
            // 1. Surrogate-side temp, seeded with the distinct base keys.
            var createTempSql = $"DROP TABLE IF EXISTS {tempRef}; CREATE TABLE {tempRef} ({tempDdl});";
            var seedSelectSql = $"SELECT DISTINCT {keyColumnsList} FROM {baseRef};";
            statements.Add(createTempSql);
            statements.Add(seedSelectSql);
            await ExecuteAsync(skeyConnectionString, createTempSql, ct).ConfigureAwait(false);
            await BulkCopyAsync(targetConnectionString, seedSelectSql, skeyConnectionString, tempRef, copyColumns, ct).ConfigureAwait(false);

            // 2. Generate the surrogates and stamp them into the surrogate-side temp.
            var generateSql = BuildGenerateSql(tempRef, skeyRef, spec, skeyColumns, flow.Target.Table.QualifiedName);
            var tempPushSql = BuildPushBackSql(tempRef, skeyRef, spec, skeyColumns);
            statements.Add(generateSql);
            statements.Add(tempPushSql);
            await using (var connection = new SqlConnection(skeyConnectionString))
            {
                await connection.OpenAsync(ct).ConfigureAwait(false);
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
                try
                {
                    await using (var command = new SqlCommand(generateSql, connection, transaction) { CommandTimeout = 0 })
                    {
                        keysGenerated = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }

                    await using (var command = new SqlCommand(tempPushSql, connection, transaction) { CommandTimeout = 0 })
                    {
                        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }

                    await transaction.CommitAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            }

            // 3. Target-side temp, seeded with the stamped keys, then pushed onto the real base.
            var returnSelectSql = $"SELECT {keyColumnsList}, [{Escape(spec.SurrogateColumn)}] FROM {tempRef};";
            var remotePushSql = BuildRemotePushBackSql(baseRef, tempRef, spec);
            statements.Add(createTempSql);
            statements.Add(returnSelectSql);
            statements.Add(remotePushSql);
            await ExecuteAsync(targetConnectionString, createTempSql, ct).ConfigureAwait(false);
            await BulkCopyAsync(skeyConnectionString, returnSelectSql, targetConnectionString, tempRef, stampedColumns, ct).ConfigureAwait(false);
            rowsStamped = await ExecuteCountAsync(targetConnectionString, remotePushSql, ct).ConfigureAwait(false);

            return (keysGenerated, rowsStamped);
        }
        finally
        {
            await TryDropAsync(skeyConnectionString, tempRef, ct).ConfigureAwait(false);
            await TryDropAsync(targetConnectionString, tempRef, ct).ConfigureAwait(false);
        }
    }

    private async Task<(string ConnectionString, bool IsRemote)> ResolveSurrogateConnectionAsync(SurrogateKeySpec spec, string targetConnectionString, CancellationToken ct)
    {
        if (spec.ConnectionReference is null)
        {
            return (targetConnectionString, false);
        }

        var resolved = await _resolver.ResolveAsync(spec.ConnectionReference, ConnectionRole.Target, ct: ct).ConfigureAwait(false);
        var isRemote = !string.Equals(resolved.CanonicalString, targetConnectionString, StringComparison.Ordinal);
        return (resolved.CanonicalString, isRemote);
    }

    private async Task<IReadOnlyDictionary<string, string>> IntrospectBaseTypesAsync(RelationalObject target, string targetConnectionString, CancellationToken ct)
    {
        await using var connection = new SqlConnection(targetConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        var live = await _catalog.IntrospectObjectAsync(connection, new ThreePartName { Database = null, Schema = target.Schema, Name = target.Name }, ct).ConfigureAwait(false)
            ?? throw new SqlFlowException($"Surrogate-key target {target.QualifiedName} was not found.");
        return live.Columns.ToDictionary(c => c.Name, c => c.NativeType, StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildGenerateSql(string baseRef, string skeyRef, SurrogateKeySpec spec, IReadOnlyList<string> skeyColumns, string baseDisplayName)
    {
        var insertColumns = string.Join(", ", skeyColumns.Select(c => $"[{Escape(c)}]"));
        var selectColumns = string.Join(", ", spec.KeyColumns.Select(c => $"src.[{Escape(c)}]"));
        var joinOn = string.Join(" AND ", spec.KeyColumns.Select((k, i) => $"src.[{Escape(k)}] = trg.[{Escape(skeyColumns[i])}]"));
        var whereMissing = $"WHERE trg.[{Escape(skeyColumns[0])}] IS NULL";

        return
            $"INSERT INTO {skeyRef} ({insertColumns}, [InsertedDate_DW], [SrcDBSchTbl]) " +
            $"SELECT DISTINCT {selectColumns}, GETDATE(), N'{Quote(baseDisplayName)}' " +
            $"FROM {baseRef} AS src LEFT OUTER JOIN {skeyRef} AS trg WITH (HOLDLOCK, UPDLOCK) ON {joinOn} {whereMissing};";
    }

    private static string BuildPushBackSql(string baseRef, string skeyRef, SurrogateKeySpec spec, IReadOnlyList<string> skeyColumns)
    {
        var joinOn = string.Join(" AND ", spec.KeyColumns.Select((k, i) => $"src.[{Escape(skeyColumns[i])}] = trg.[{Escape(k)}]"));
        return
            $"UPDATE trg SET trg.[{Escape(spec.SurrogateColumn)}] = src.[{Escape(spec.SurrogateColumn)}] " +
            $"FROM {baseRef} AS trg INNER JOIN {skeyRef} AS src ON {joinOn} WHERE trg.[{Escape(spec.SurrogateColumn)}] IS NULL;";
    }

    private static string BuildRemotePushBackSql(string baseRef, string tempRef, SurrogateKeySpec spec)
    {
        var joinOn = string.Join(" AND ", spec.KeyColumns.Select(k => $"src.[{Escape(k)}] = trg.[{Escape(k)}]"));
        return
            $"UPDATE trg SET trg.[{Escape(spec.SurrogateColumn)}] = src.[{Escape(spec.SurrogateColumn)}] " +
            $"FROM {baseRef} AS trg INNER JOIN {tempRef} AS src ON {joinOn} WHERE trg.[{Escape(spec.SurrogateColumn)}] IS NULL;";
    }

    private static string BuildEnsureSurrogateTableSql(SurrogateKeySpec spec, IReadOnlyList<string> skeyColumns, IReadOnlyDictionary<string, string> baseTypes)
    {
        var keyColumnDdl = string.Join(", ", spec.KeyColumns.Select((k, i) => $"[{Escape(skeyColumns[i])}] {TypeOf(baseTypes, k)} NULL"));
        var uniqueColumns = string.Join(", ", skeyColumns.Select(c => $"[{Escape(c)}]"));
        var schema = Escape(spec.SurrogateTable.Schema);
        var name = Escape(spec.SurrogateTable.Name);

        var inner =
            $"IF OBJECT_ID(N'[{schema}].[{name}]', N'U') IS NULL " +
            $"CREATE TABLE [{schema}].[{name}] (" +
            $"[{Escape(spec.SurrogateColumn)}] int IDENTITY(1,1) NOT NULL PRIMARY KEY, " +
            $"{keyColumnDdl}, [InsertedDate_DW] datetime NULL, [SrcDBSchTbl] nvarchar(250) NULL, UNIQUE ({uniqueColumns}));";

        // Created in the surrogate table's own database via that database's sp_executesql, so the connection's
        // current database does not have to match (legacy SMO connected to the specific database).
        return $"EXEC [{Escape(spec.SurrogateTable.Database)}].sys.sp_executesql N'{Quote(inner)}';";
    }

    private static string BuildEnsureBaseColumnSql(RelationalObject target, string surrogateColumn)
    {
        var baseRef = TwoPart(target);
        var column = Escape(surrogateColumn);
        var createIndex = $"CREATE NONCLUSTERED INDEX [NCI_{column}] ON {baseRef} ([{column}]);";

        return
            $"IF COL_LENGTH(N'{Quote(baseRef)}', N'{Quote(surrogateColumn)}') IS NULL ALTER TABLE {baseRef} ADD [{column}] int NULL; " +
            $"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'{Quote(baseRef)}') AND name = N'NCI_{column}') " +
            $"EXEC sys.sp_executesql N'{Quote(createIndex)}';";
    }

    private static async Task BulkCopyAsync(string fromConnectionString, string selectSql, string toConnectionString, string destinationTable, IReadOnlyList<string> columns, CancellationToken ct)
    {
        await using var source = new SqlConnection(fromConnectionString);
        await source.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand(selectSql, source) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        await using var destination = new SqlConnection(toConnectionString);
        await destination.OpenAsync(ct).ConfigureAwait(false);
        using var bulkCopy = new SqlBulkCopy(destination) { DestinationTableName = destinationTable, BulkCopyTimeout = 0, EnableStreaming = true };
        foreach (var column in columns)
        {
            bulkCopy.ColumnMappings.Add(column, column);
        }

        await bulkCopy.WriteToServerAsync(reader, ct).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<long> ExecuteCountAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task TryDropAsync(string connectionString, string tableRef, CancellationToken ct)
    {
        try
        {
            await ExecuteAsync(connectionString, $"DROP TABLE IF EXISTS {tableRef};", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort cleanup; a leftover run-scoped temp does not collide on the next run.
        }
    }

    private static string TypeOf(IReadOnlyDictionary<string, string> baseTypes, string column)
        => baseTypes.TryGetValue(column, out var type)
            ? type
            : throw new SqlFlowException($"Surrogate-key business-key column '{column}' is not on the target table.");

    private static bool Gate(string? value) => value is not null && value.Trim().Length > 2;

    private static string TwoPart(RelationalObject o) => $"[{Escape(o.Schema)}].[{Escape(o.Name)}]";

    private static string ThreePart(RelationalObject o) => $"[{Escape(o.Database)}].[{Escape(o.Schema)}].[{Escape(o.Name)}]";

    private static string Escape(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);

    private static string Quote(string literal) => literal.Replace("'", "''", StringComparison.Ordinal);
}
