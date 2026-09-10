using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Core.Query;

namespace SqlFlow.SqlServer.Query;

/// <summary>
/// Runs one already-approved read-only query and returns its rows.
///
/// Two independent guarantees keep this read-only, and it needs both. <see cref="ReadOnlyQueryGuard"/> has
/// already proved the statement is a single SELECT by parsing it. On top of that, execution happens inside a
/// transaction that is ALWAYS rolled back, so even a statement that somehow slipped past the parser cannot
/// leave anything behind. Belt and braces is warranted here because the cost of being wrong is data.
///
/// Every bound is enforced rather than requested: rows stop at the cap with the result marked truncated, the
/// command carries a real timeout, and oversized cell values are trimmed so one <c>varchar(max)</c> column
/// cannot turn a small result into an unreadable one.
/// </summary>
public static class SqlServerQueryRunner
{
    /// <summary>The longest a single cell is carried before it is trimmed with a marker.</summary>
    public const int MaxCellChars = 4000;

    public static async Task<QueryResult> RunAsync(
        string connectionString, QueryRunRequest request, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(request);

        // Re-validated here even though the control plane validated at prepare: the queue row is data from
        // the database, and this is the last point before the statement reaches an engine.
        var sql = ReadOnlyQueryGuard.Validate(request.Sql);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(request.Database))
        {
            await connection.ChangeDatabaseAsync(request.Database, ct).ConfigureAwait(false);
        }

        // READ COMMITTED SNAPSHOT is not assumed; the transaction exists to guarantee rollback, not isolation.
        await using var transaction = (SqlTransaction)await connection
            .BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.CommandTimeout = request.TimeoutSeconds;

            var columns = new List<QueryColumn>();
            var rows = new List<IReadOnlyList<string?>>();
            var truncated = false;

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                columns.Add(new QueryColumn(reader.GetName(i), reader.GetDataTypeName(i)));
            }

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (rows.Count >= request.MaxRows)
                {
                    // Stop reading rather than reading everything and slicing: the point of the cap is to not
                    // pull the rest across the wire.
                    truncated = true;
                    break;
                }

                var row = new string?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[i] = reader.IsDBNull(i) ? null : Render(reader.GetValue(i));
                }

                rows.Add(row);
            }

            return new QueryResult
            {
                Columns = columns,
                Rows = rows,
                RowCount = rows.Count,
                Truncated = truncated,
                Database = connection.Database,
                Sql = sql,
            };
        }
        finally
        {
            // ALWAYS rolled back, on every path including an exception. Nothing this runs can persist.
            try
            {
                await transaction.RollbackAsync(ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // The transaction is already gone (the connection dropped, or the server aborted it). There is
                // nothing left to roll back, and failing here would mask the real error from the caller.
            }
        }
    }

    /// <summary>Renders one cell as text, invariantly so a number or a date reads the same wherever the node
    /// runs, and trimmed so one oversized column cannot swamp the result.</summary>
    private static string Render(object value)
    {
        var text = value switch
        {
            DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset offset => offset.ToString("O", CultureInfo.InvariantCulture),
            byte[] bytes => "0x" + Convert.ToHexString(bytes),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

        return text.Length <= MaxCellChars
            ? text
            : text[..MaxCellChars] + $"... [{text.Length - MaxCellChars} more characters]";
    }
}
