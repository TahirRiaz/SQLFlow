namespace SqlFlow.Core;

/// <summary>
/// Thrown when a flow cannot start because another execution of the SAME flow already holds its work tables.
/// A flow's staging and match-key tables are named per flow (one canonical table per flow id, rebuilt by every
/// run), so two overlapping executions do not merely queue: each drops and recreates the table the other is
/// filling, and the surviving one loads whatever rows happened to be in it. The run therefore stops here, before
/// any source read or target write, rather than corrupting its sibling.
/// <para>Reaching this is not routine. The run queue already serializes runs of one pipeline, so an occurrence
/// means the execution came from somewhere the queue does not gate: a run re-claimed after its node was presumed
/// dead (the original is still executing), a direct CLI or local run against a target a scheduled run is loading,
/// or one flow file registered under two pipelines. The failure is safe and retryable: nothing was written, and
/// the execution that holds the lease is doing the work.</para>
/// </summary>
public sealed class ConcurrentFlowExecutionException : SqlFlowException
{
    public ConcurrentFlowExecutionException(string flow, string workTable, int waitedMs, int returnCode)
        : base($"Flow '{flow}' is already being executed: its work table {workTable} is held by another execution "
               + $"(waited {waitedMs}ms; sp_getapplock returned {returnCode}). Two executions of one flow share that "
               + "table, so this run stopped before reading the source or writing the target. Retry once the other "
               + "execution finishes.")
    {
        Flow = flow;
        WorkTable = workTable;
        WaitedMs = waitedMs;
        ReturnCode = returnCode;
    }

    /// <summary>The flow whose work tables are held (its alias, or the target table when it has none).</summary>
    public string Flow { get; }

    /// <summary>The schema-qualified staging table the lease names.</summary>
    public string WorkTable { get; }

    /// <summary>How long the run waited for the holder to finish before giving up.</summary>
    public int WaitedMs { get; }

    /// <summary>sp_getapplock's code: -1 timeout, -2 cancelled, -3 deadlock victim, -999 parameter error.</summary>
    public int ReturnCode { get; }
}
