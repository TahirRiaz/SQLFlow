using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Core.Ingestion;

namespace SqlFlow.SqlServer.Ingestion;

/// <summary>
/// An exclusive lease on ONE flow's work tables, held for the whole of a run.
/// <para>An ingestion flow stages through a canonical work table named for the flow it belongs to
/// (<c>[raw].[schema_table_flowId]</c>, plus the match-key table beside it), rebuilt by every run so executions
/// never accumulate per-run copies. That naming assumes at most one execution of a flow at a time, and nothing in
/// the engine used to enforce it: two overlapping executions each ran DROP-then-CREATE over the same name, so one
/// deleted the table the other had just filled, and a run could load rows its sibling staged. The run queue gates
/// on it, but the queue is not the only way a flow executes (a re-claimed run whose node was presumed dead, a CLI
/// run, one flow file registered twice), so the guarantee belongs here, at the table it protects.</para>
/// <para>The lease is a session-owned SQL Server application lock on the target database, keyed by the work-table
/// name. Session-owned is what lets it span the run's many transactions, and what makes it self-healing: a node
/// that dies mid-run frees the lease when its connection drops, so a flow can never be wedged by a crash. The
/// dedicated connection exists only to own the lease, and the lease is released and the connection closed on every
/// exit path.</para>
/// </summary>
internal sealed class WorkTableLease : IAsyncDisposable
{
    /// <summary>How long a run waits for a sibling execution to release the work tables before failing. Long enough
    /// to absorb the overlap of two executions started at nearly the same moment (the tail of one run's load), short
    /// enough that a run does not sit on a worker slot behind a long execution it can never usefully follow: the
    /// holder is doing this run's work, so failing and letting the schedule pick it up again is the better outcome.
    /// Matches the schema applier's app-lock wait, so both of a run's locks fail on the same budget.</summary>
    internal const int LockWaitMs = 30_000;

    private readonly SqlConnection _connection;

    private WorkTableLease(SqlConnection connection, string resource, string workTable, bool waited, int elapsedMs)
    {
        _connection = connection;
        Resource = resource;
        WorkTable = workTable;
        Waited = waited;
        WaitedMs = elapsedMs;
    }

    /// <summary>The application-lock resource this lease holds (one per flow work table, per target database).</summary>
    public string Resource { get; }

    /// <summary>The schema-qualified staging table the lease protects.</summary>
    public string WorkTable { get; }

    /// <summary>True when the lease was not free on arrival: a sibling execution was finishing and this run queued
    /// behind it rather than clobbering it, which is worth saying in the run's trace. Taken from sp_getapplock's own
    /// answer (granted immediately versus granted after waiting), never inferred from elapsed time, which also
    /// counts the round trip.</summary>
    public bool Waited { get; }

    /// <summary>How long the acquire took end to end, in milliseconds. Meaningful as a wait only alongside
    /// <see cref="Waited"/>; on a free lease it is just the round trip.</summary>
    public int WaitedMs { get; }

    /// <summary>
    /// Takes the lease, waiting at most <paramref name="waitMs"/> for a sibling execution to finish.
    /// </summary>
    /// <param name="targetConnectionString">The target database: the lease lives where the work tables do.</param>
    /// <param name="staging">The flow's canonical staging table, which names the resource.</param>
    /// <param name="flow">The flow's label for the error message (its alias, or its target table).</param>
    /// <param name="waitMs">How long to wait for a holder, normally <see cref="LockWaitMs"/>.</param>
    /// <param name="ct">Cancels the wait; the half-taken lease is released with its connection.</param>
    /// <exception cref="ConcurrentFlowExecutionException">Another execution of this flow still holds the work
    /// tables. Nothing has been read or written at this point, so the run fails clean.</exception>
    public static async Task<WorkTableLease> AcquireAsync(
        string targetConnectionString, RelationalObject staging, string flow, int waitMs, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetConnectionString);
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentException.ThrowIfNullOrWhiteSpace(flow);
        ArgumentOutOfRangeException.ThrowIfNegative(waitMs);

        var resource = ResourceFor(staging);
        var workTable = $"[{staging.Schema}].[{staging.Name}]";
        var connection = new SqlConnection(targetConnectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var elapsed = Stopwatch.StartNew();
            var code = await SqlAppLock.TryAcquireAsync(connection, resource, waitMs, ct).ConfigureAwait(false);
            elapsed.Stop();
            if (code < 0)
            {
                throw new ConcurrentFlowExecutionException(flow, workTable, (int)elapsed.ElapsedMilliseconds, code);
            }

            return new WorkTableLease(
                connection, resource, workTable, code == SqlAppLock.GrantedAfterWait, (int)elapsed.ElapsedMilliseconds);
        }
        catch
        {
            // Nothing here may leak a connection, and disposing it is also what frees a lease that was granted on
            // the round trip a cancellation abandoned.
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The lock resource for a flow's work tables: the staging table's own name, case-canonical so a
    /// case-insensitive server cannot yield two resources for one table, and prefixed so it can never collide with
    /// the schema applier's object locks. Application locks are database-scoped, and the lease is taken on the
    /// target connection, so the same flow name in two databases is two independent resources.</summary>
    internal static string ResourceFor(RelationalObject staging)
    {
        ArgumentNullException.ThrowIfNull(staging);
        return "SqlFlow.WorkTables:"
            + staging.Schema.ToLower(CultureInfo.InvariantCulture)
            + "."
            + staging.Name.ToLower(CultureInfo.InvariantCulture);
    }

    /// <summary>Releases the lease and closes its connection. Safe on every exit path, including one already
    /// unwinding an exception: the release is best-effort and the close frees the session lock regardless.</summary>
    public async ValueTask DisposeAsync()
    {
        await SqlAppLock.ReleaseAsync(_connection, Resource).ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
