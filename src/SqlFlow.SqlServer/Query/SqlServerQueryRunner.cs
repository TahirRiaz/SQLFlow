using System.Data;
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
        var columns = new List<QueryColumn>();
        var rows = new List<IReadOnlyList<string?>>();
        var truncated = false;
        var database = await ExecuteAsync(
            connectionString, sql, request.Database, NoParameters, request.TimeoutSeconds,
            async (reader, token) =>
            {
                columns.AddRange(Columns(reader));
                while (await reader.ReadAsync(token).ConfigureAwait(false))
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
            },
            ct).ConfigureAwait(false);

        return new QueryResult
        {
            Columns = columns,
            Rows = rows,
            RowCount = rows.Count,
            Truncated = truncated,
            Database = database,
            Sql = sql,
        };
    }

    /// <summary>
    /// Runs one read-only query that binds named parameters, and hands each row to <paramref name="row"/> with the
    /// provider's own values, untrimmed, until the rows run out or it answers false: for a caller that compares
    /// what a query answers with values held elsewhere, where a cell rendered or trimmed for display would compare
    /// wrongly. The statement is parsed by <see cref="ReadOnlyQueryGuard"/> first, each parameter is sent as a
    /// value and never written into the text, and the statement runs inside a transaction that is always rolled
    /// back, as <see cref="RunAsync"/> runs one. Returns how many rows were handed over.
    /// </summary>
    /// <exception cref="SqlFlowException">The statement is not one read-only SELECT, or a parameter or the timeout is out of bounds.</exception>
    public static async Task<long> ReadAsync(
        string connectionString, ReadOnlyRead read, Func<IReadOnlyList<QueryColumn>, IDataRecord, bool> row, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(row);
        var sql = ReadOnlyQueryGuard.Validate(read.Sql);
        read.Validate();
        long handed = 0;
        await ExecuteAsync(
            connectionString, sql, null, read.Parameters, read.TimeoutSeconds,
            async (reader, token) =>
            {
                var columns = Columns(reader);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    handed++;
                    if (!row(columns, reader))
                    {
                        break;
                    }
                }
            },
            ct).ConfigureAwait(false);
        return handed;
    }

    private static readonly IReadOnlyDictionary<string, string?> NoParameters = new Dictionary<string, string?>(StringComparer.Ordinal);

    private static List<QueryColumn> Columns(SqlDataReader reader)
    {
        var columns = new List<QueryColumn>(reader.FieldCount);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            columns.Add(new QueryColumn(reader.GetName(i), reader.GetDataTypeName(i)));
        }

        return columns;
    }

    /// <summary>
    /// Opens the connection, runs <paramref name="sql"/> (already validated) inside a transaction that is always
    /// rolled back, with <paramref name="parameters"/> bound as <c>nvarchar</c> values, and hands the reader to
    /// <paramref name="consume"/>. Returns the database the statement ran in.
    /// </summary>
    private static async Task<string> ExecuteAsync(
        string connectionString, string sql, string? database, IReadOnlyDictionary<string, string?> parameters, int timeoutSeconds,
        Func<SqlDataReader, CancellationToken, Task> consume, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(database))
        {
            await connection.ChangeDatabaseAsync(database, ct).ConfigureAwait(false);
        }

        // READ COMMITTED SNAPSHOT is not assumed; the transaction exists to guarantee rollback, not isolation.
        await using var transaction = (SqlTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.CommandTimeout = timeoutSeconds;
            foreach (var (name, value) in parameters)
            {
                var text = value ?? string.Empty;
                command.Parameters.Add(new SqlParameter("@" + name, SqlDbType.NVarChar, text.Length <= 4000 ? 4000 : -1)
                {
                    Value = value is null ? DBNull.Value : value,
                });
            }

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await consume(reader, ct).ConfigureAwait(false);
            return connection.Database;
        }
        finally
        {
            // ALWAYS rolled back, on every path including an exception, and whether or not the caller was
            // cancelled: the rollback itself is never cancelled. Nothing this runs can persist.
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
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
