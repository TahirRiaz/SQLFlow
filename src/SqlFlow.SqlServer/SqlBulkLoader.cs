using System.Data.Common;
using Microsoft.Data.SqlClient;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;

namespace SqlFlow.SqlServer;

/// <summary>Loads rows into the target using SqlBulkCopy - the fastest path into SQL Server.</summary>
public sealed class SqlBulkLoader : IBulkLoader
{
    public async Task<long> LoadAsync(string connectionString, TargetSpec target, DbDataReader data, LoadPolicy load, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(load);

        await using var connection = new SqlConnection(BulkTuning.ForBulk(connectionString));
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var options = load.TableLock ? SqlBulkCopyOptions.TableLock : SqlBulkCopyOptions.Default;
        using var bulk = new SqlBulkCopy(connection, options, externalTransaction: null)
        {
            DestinationTableName = target.QualifiedName,
            BatchSize = load.BatchSize,
            BulkCopyTimeout = 0,
            EnableStreaming = true,
        };

        for (var i = 0; i < data.FieldCount; i++)
        {
            var name = data.GetName(i);
            bulk.ColumnMappings.Add(name, name);
        }

        await bulk.WriteToServerAsync(data, ct).ConfigureAwait(false);
        return bulk.RowsCopied64;
    }

    public async Task TruncateAsync(string connectionString, TargetSpec target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        // TRUNCATE needs a schema-modification lock, so it queues behind every reader and writer on the table.
        // The wait is bounded by the server's lock timeout, never by a client clock.
        await using var command = new SqlCommand($"TRUNCATE TABLE {target.QualifiedName};", connection) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
