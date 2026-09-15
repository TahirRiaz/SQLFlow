using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Dispatch.Protocol;
using SqlFlow.Orchestration;

namespace SqlFlow.Node;

/// <summary>
/// The fan-out a node offers the run it executes (<see cref="DocumentExecutionOptions.FanOut"/>). Every call travels
/// the node protocol under the run's hand-out fence and is retried while the dispatcher is momentarily unreachable or
/// not the owner. A call the dispatcher answers as no longer held means the run's lease lapsed and the run was
/// requeued: the successor execution owns the fan-out from then on, so this execution is told so with an error rather
/// than handed an answer it could act on.
/// </summary>
internal sealed partial class NodeRunFanOut : IRunFanOut
{
    private readonly INodeTransport _transport;
    private readonly Guid _runId;
    private readonly string _node;
    private readonly int _attempt;
    private readonly ILogger _logger;

    public NodeRunFanOut(INodeTransport transport, Guid runId, string node, int attempt, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        ArgumentNullException.ThrowIfNull(logger);
        _transport = transport;
        _runId = runId;
        _node = node;
        _attempt = attempt;
        _logger = logger;
    }

    public async Task<RunFanOutHandle> EnqueueAsync(IReadOnlyList<RunParameters> members, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(members);
        var request = new FanOutRequest(_node, _attempt, members);
        request.Validate();
        var response = await CallAsync(
            "fan-out", token => _transport.EnqueueFanOutAsync(_runId, request, token), ct).ConfigureAwait(false);
        return response is { Held: true, GroupId: { } groupId }
            ? new RunFanOutHandle(groupId, response.RunIds)
            : throw NotHeld();
    }

    public async Task<IReadOnlyList<RunFanOutMember>> MembersAsync(RunFanOutHandle handle, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var response = await CallAsync(
            "fan-out state",
            token => _transport.GetFanOutStateAsync(_runId, handle.GroupId, new FanOutFence(_node, _attempt), token),
            ct).ConfigureAwait(false);
        if (!response.Held)
        {
            throw NotHeld();
        }

        return response.Members
            .Select(m => new RunFanOutMember(m.RunId, m.Slot, m.Status, m.Error, m.ResultJson))
            .ToList();
    }

    public async Task CancelAsync(RunFanOutHandle handle, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var response = await CallAsync(
            "fan-out cancel",
            token => _transport.CancelFanOutAsync(_runId, handle.GroupId, new FanOutFence(_node, _attempt), token),
            ct).ConfigureAwait(false);
        if (!response.Held)
        {
            throw NotHeld();
        }
    }

    private Task<T> CallAsync<T>(string what, Func<CancellationToken, Task<T>> call, CancellationToken ct)
        => DispatcherRetry.RunAsync(
            DispatcherRetry.SupportWaits, call,
            (ex, wait) => LogFanOutRetry(_runId, what, SecretHygiene.RedactedMessage(ex), (int)wait.TotalSeconds), ct);

    private SqlFlowException NotHeld()
        => new($"run {_runId} no longer carries this node's lease at attempt {_attempt} (it lapsed and the run was "
            + "requeued), so its fan-out belongs to the successor execution.");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId}: the {What} call could not reach the dispatcher ({Error}); retrying in about {WaitSeconds}s.")]
    private partial void LogFanOutRetry(Guid runId, string what, string error, int waitSeconds);
}
