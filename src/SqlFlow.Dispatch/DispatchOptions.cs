namespace SqlFlow.Dispatch;

/// <summary>The dispatcher's timing knobs. Every value is a plain number so a host can bind it from configuration;
/// <see cref="Validate"/> rejects a combination that could not work (a lease shorter than a poll, a wait longer
/// than the protocol allows).</summary>
public sealed class DispatchOptions
{
    /// <summary>How long a hand-out's lease lasts without renewal. A node renews on every poll, so this only has to
    /// outlast a few missed polls plus the longest long-poll wait; a lease that expires while the node is still
    /// executing requeues the run and consumes one of its attempts.</summary>
    public int LeaseSeconds { get; set; } = 90;

    /// <summary>The longest a poll with free slots is held open waiting for work or a signal. Capped by
    /// <see cref="Protocol.NodeProtocol.MaxWaitSeconds"/>.</summary>
    public int LongPollSeconds { get; set; } = 30;

    /// <summary>How often memory is diffed against the ledger, the self-healing path for anything that bypassed the
    /// in-process notify (a direct catalog cancel, an enqueue on a passive replica).</summary>
    public int ReconcileSeconds { get; set; } = 5;

    /// <summary>How often the in-memory node registry is flushed to the ledger for the fleet view.</summary>
    public int NodeFlushSeconds { get; set; } = 5;

    /// <summary>How long a node may be silent before it leaves the registry (and its ledger row is pruned). Zero
    /// keeps rows until an operator deletes them.</summary>
    public int NodeRetentionHours { get; set; } = 24;

    /// <summary>How long a queued compute task may wait for a node before it is failed with a routing hint.</summary>
    public int TaskQueuedExpiryMinutes { get; set; } = 15;

    /// <summary>How long a compute task may execute before it is presumed lost and failed.</summary>
    public int TaskRunningExpiryHours { get; set; } = 6;

    /// <summary>How many times a run may be handed out before an interrupted attempt is failed instead of requeued.
    /// Three distinguishes "unlucky twice" from "the run is the cause".</summary>
    public int MaxExecutionAttempts { get; set; } = 3;

    /// <summary>The TTL of the single dispatch ownership lease shared by every control-plane replica.</summary>
    public int OwnershipTtlSeconds { get; set; } = 30;

    /// <summary>How often the owner renews the ownership lease (and a passive replica retries acquiring it).</summary>
    public int OwnershipRenewSeconds { get; set; } = 10;

    public TimeSpan Lease => TimeSpan.FromSeconds(LeaseSeconds);

    public TimeSpan LongPoll => TimeSpan.FromSeconds(LongPollSeconds);

    public TimeSpan Reconcile => TimeSpan.FromSeconds(ReconcileSeconds);

    public TimeSpan NodeFlush => TimeSpan.FromSeconds(NodeFlushSeconds);

    public TimeSpan NodeRetention => TimeSpan.FromHours(NodeRetentionHours);

    public TimeSpan TaskQueuedExpiry => TimeSpan.FromMinutes(TaskQueuedExpiryMinutes);

    public TimeSpan TaskRunningExpiry => TimeSpan.FromHours(TaskRunningExpiryHours);

    public TimeSpan OwnershipTtl => TimeSpan.FromSeconds(OwnershipTtlSeconds);

    public TimeSpan OwnershipRenew => TimeSpan.FromSeconds(OwnershipRenewSeconds);

    /// <summary>Rejects a combination that cannot work, naming the offending setting.</summary>
    public void Validate()
    {
        if (LongPollSeconds is < 1 or > Protocol.NodeProtocol.MaxWaitSeconds)
        {
            throw new InvalidOperationException(
                $"Dispatch:LongPollSeconds must be between 1 and {Protocol.NodeProtocol.MaxWaitSeconds}.");
        }

        if (LeaseSeconds < 2 * LongPollSeconds)
        {
            throw new InvalidOperationException(
                "Dispatch:LeaseSeconds must be at least twice Dispatch:LongPollSeconds, so a node that is held in one long poll and then misses one never loses its lease.");
        }

        if (ReconcileSeconds < 1 || NodeFlushSeconds < 1)
        {
            throw new InvalidOperationException("Dispatch:ReconcileSeconds and Dispatch:NodeFlushSeconds must be positive.");
        }

        if (NodeRetentionHours < 0)
        {
            throw new InvalidOperationException("Dispatch:NodeRetentionHours must be zero or positive (0 disables pruning).");
        }

        if (TaskQueuedExpiryMinutes < 1 || TaskRunningExpiryHours < 1)
        {
            throw new InvalidOperationException("Dispatch:TaskQueuedExpiryMinutes and Dispatch:TaskRunningExpiryHours must be positive.");
        }

        if (MaxExecutionAttempts < 1)
        {
            throw new InvalidOperationException("Dispatch:MaxExecutionAttempts must be at least 1.");
        }

        if (OwnershipRenewSeconds < 1 || OwnershipTtlSeconds < 2 * OwnershipRenewSeconds)
        {
            throw new InvalidOperationException(
                "Dispatch:OwnershipTtlSeconds must be at least twice Dispatch:OwnershipRenewSeconds, so one missed renewal never loses ownership.");
        }
    }
}
