using System.Data;
using Microsoft.Data.SqlClient;

namespace SqlFlow.SqlServer;

/// <summary>
/// The engine's one implementation of SQL Server application locks (<c>sys.sp_getapplock</c> /
/// <c>sys.sp_releaseapplock</c>), the mutex two SqlFlow executions use to agree on a shared database object.
/// Session-owned throughout: the lock lives with the connection, not with a transaction, so it can span the many
/// transactions a run performs and is freed the moment the connection closes (or is reset back into the pool) even
/// if a process dies without releasing it. Callers map the return code to their own domain error, which is why this
/// reports the code rather than throwing: <see cref="SqlServerSchemaProvider"/> raises a schema-lock timeout, an
/// ingestion run raises a concurrent-execution error naming the flow.
/// </summary>
internal static class SqlAppLock
{
    /// <summary>Return codes: 0 granted immediately, 1 granted after waiting, -1 timeout, -2 cancelled,
    /// -3 deadlock victim, -999 parameter/validation error. Anything below zero means the lock is NOT held.</summary>
    public const int GrantedImmediately = 0;
    public const int GrantedAfterWait = 1;
    public const int ParameterError = -999;

    /// <summary>Takes the exclusive, session-owned lock on <paramref name="resource"/>, waiting at most
    /// <paramref name="timeoutMs"/> milliseconds, and returns sp_getapplock's code. A negative code means the lock
    /// was not granted and the caller must not proceed.</summary>
    public static async Task<int> TryAcquireAsync(
        SqlConnection connection, string resource, int timeoutMs, CancellationToken ct)
    {
        // The wait is bounded server-side by @LockTimeout, which is the whole point of this call: on expiry
        // sp_getapplock returns -1 and the caller raises a typed, retryable error. A client CommandTimeout must
        // never be the shorter of the two, or it aborts the round trip first and the graceful path becomes
        // unreachable. ADO.NET's 30 second default is exactly the schema applier's default wait, so the client
        // always won that race; 0 hands the bound back to @LockTimeout where it belongs.
        await using var command = new SqlCommand("sys.sp_getapplock", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 0,
        };
        command.Parameters.AddWithValue("@Resource", resource);
        command.Parameters.AddWithValue("@LockMode", "Exclusive");
        command.Parameters.AddWithValue("@LockOwner", "Session");
        command.Parameters.AddWithValue("@LockTimeout", timeoutMs);
        var returnValue = command.Parameters.Add("@ReturnValue", SqlDbType.Int);
        returnValue.Direction = ParameterDirection.ReturnValue;

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return returnValue.Value is int code ? code : ParameterError;
    }

    /// <summary>Releases the session-owned lock, best-effort: closing the connection frees it anyway, so a release
    /// failure must never mask the exception the caller is already unwinding with. Silent on a connection that is
    /// no longer open for the same reason.</summary>
    public static async Task ReleaseAsync(SqlConnection connection, string resource)
    {
        if (connection.State != ConnectionState.Open)
        {
            return;
        }

        try
        {
            await using var command = new SqlCommand("sys.sp_releaseapplock", connection)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = 0,
            };
            command.Parameters.AddWithValue("@Resource", resource);
            command.Parameters.AddWithValue("@LockOwner", "Session");
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (SqlException)
        {
            // The connection close that follows frees the session lock anyway.
        }
        catch (InvalidOperationException)
        {
            // The connection was closed or broken from under the release (a severed link, a disposed owner); the
            // lock is gone with the session, which is the outcome this call wanted.
        }
    }
}
