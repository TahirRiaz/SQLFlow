using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace SqlFlow.Catalog;

/// <summary>
/// A transaction at a raised isolation level, on a connection that goes back to the pool afterwards. SQL Server keeps a
/// session's isolation level when the pool lends the connection again (<c>sp_reset_connection</c> resets the session's
/// other options, not this one), so once a SERIALIZABLE or REPEATABLE READ transaction ends, every statement the next
/// borrower runs outside a transaction of its own runs at that level, and so does every transaction it begins without
/// naming one. A plain read then takes range locks and holds them to its statement's end, ignores the database's
/// read-committed snapshot, and becomes a deadlock victim of the writers it should never have blocked. A unit that
/// raises the level therefore lowers it again before the connection leaves it.
/// </summary>
public static class PooledIsolation
{
    private const string ReadCommitted = "SET TRANSACTION ISOLATION LEVEL READ COMMITTED;";

    /// <summary>
    /// Runs <paramref name="work"/> in one transaction at <paramref name="level"/>, inside the context's execution
    /// strategy so a transient failure (a deadlock victim) replays the whole unit, and leaves the connection at READ
    /// COMMITTED whether the unit commits or fails. The change tracker is cleared before each attempt, so the work must
    /// stage its changes afresh each call; what it leaves tracked is saved before the commit.
    /// </summary>
    /// <remarks>
    /// The level is lowered as the unit's last statement, before its commit: what the unit read stays protected at
    /// <paramref name="level"/> until the commit releases it, and a failure to lower it rolls the unit back like any other
    /// failure of the unit, so a committed unit is never replayed. A unit that fails lowers it before its transaction is
    /// disposed: SQL Server keeps the level through a rollback, and a statement sent after the server rolled the
    /// transaction back (as it does a deadlock victim's) runs outside it.
    /// </remarks>
    public static Task<T> InTransactionAsync<T>(DbContext context, IsolationLevel level, Func<Task<T>> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(work);
        if (level is not (IsolationLevel.Serializable or IsolationLevel.RepeatableRead))
        {
            throw new ArgumentOutOfRangeException(
                nameof(level), level, "A unit runs at SERIALIZABLE or REPEATABLE READ; a READ COMMITTED transaction needs no restoring.");
        }

        var strategy = context.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var transaction = await context.Database.BeginTransactionAsync(level, ct).ConfigureAwait(false);
            T result;
            try
            {
                result = await work().ConfigureAwait(false);
                await context.SaveChangesAsync(ct).ConfigureAwait(false);
                await RestoreReadCommittedAsync(context.Database.GetDbConnection(), transaction.GetDbTransaction(), ct).ConfigureAwait(false);
            }
            catch (Exception) when (context.Database.GetDbConnection().State == ConnectionState.Open)
            {
                await RestoreAfterFailureAsync(context.Database.GetDbConnection(), transaction.GetDbTransaction()).ConfigureAwait(false);
                throw;
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return result;
        });
    }

    /// <summary>
    /// Sets <paramref name="connection"/>'s session back to READ COMMITTED, for a caller that ran a transaction at another
    /// level on a connection of its own. Inside <paramref name="transaction"/> when one is still open on the connection
    /// (statements after it run at READ COMMITTED; what was read before stays protected to the commit), otherwise on the
    /// connection alone. Throws what the statement throws.
    /// </summary>
    public static async Task RestoreReadCommittedAsync(DbConnection connection, DbTransaction? transaction, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = ReadCommitted;
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets <paramref name="connection"/>'s session back to READ COMMITTED while a failure of a transaction at another
    /// level is being handled, and never throws in that failure's place. Call it before the transaction is disposed, with
    /// the transaction (or null when none was begun): a statement sent on a transaction the server already rolled back
    /// runs outside it. A connection that cannot take the statement is broken, and its whole pool is cleared rather than
    /// trusted to discard it, so no session at the raised level is lent again.
    /// </summary>
    public static async Task RestoreAfterFailureAsync(DbConnection connection, DbTransaction? transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        try
        {
            await RestoreReadCommittedAsync(connection, transaction, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            if (connection is SqlConnection sql)
            {
                SqlConnection.ClearPool(sql);
            }
        }
    }
}
