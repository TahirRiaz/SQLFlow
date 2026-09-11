using SqlFlow.Dispatch;
using SqlFlow.Dispatch.Protocol;

namespace SqlFlow.Node;

/// <summary>
/// How a node reaches the dispatcher. The control plane's own node calls the dispatcher in-process
/// (<see cref="InProcessNodeTransport"/>); a standalone <c>sqlflow worker</c> speaks the node protocol over HTTP
/// (<see cref="HttpNodeTransport"/>). One drain loop, two transports, one protocol.
/// </summary>
public interface INodeTransport
{
    /// <summary>The node's poll: heartbeat, lease renewal, cancel channel and hand-out in one call, held open by the
    /// dispatcher for up to the requested wait when nothing is available. Throws
    /// <see cref="NodeTransportException"/> (or <see cref="DispatchInactiveException"/> in-process) when the
    /// dispatcher cannot be reached or is not the owner; the caller retries with backoff.</summary>
    Task<NodePollResponse> PollAsync(NodePollRequest request, CancellationToken ct);

    /// <summary>Reports a run's outcome under the hand-out's fence.</summary>
    Task<RunOutcomeStatus> ReportRunOutcomeAsync(Guid runId, RunOutcomeRequest request, CancellationToken ct);

    /// <summary>Reports a compute task's outcome. Returns whether the dispatcher recorded it (false when the task no
    /// longer belonged to this node).</summary>
    Task<bool> ReportTaskOutcomeAsync(Guid taskId, TaskOutcomeRequest request, CancellationToken ct);
}

/// <summary>A node call that did not reach or was refused by the dispatcher: the HTTP status when there was one,
/// and whether the node should simply retry (an unreachable host, a 503 from a passive replica, a 5xx) or treat the
/// failure as final (a 4xx, which means the request itself was wrong).</summary>
public sealed class NodeTransportException : Exception
{
    public NodeTransportException()
    {
    }

    public NodeTransportException(string message)
        : base(message)
    {
    }

    public NodeTransportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public NodeTransportException(string message, int? statusCode, bool retryable)
        : base(message)
    {
        StatusCode = statusCode;
        Retryable = retryable;
    }

    /// <summary>The HTTP status the dispatcher answered with, or null when the call never got an answer.</summary>
    public int? StatusCode { get; }

    /// <summary>Whether a retry is the right response.</summary>
    public bool Retryable { get; } = true;
}
