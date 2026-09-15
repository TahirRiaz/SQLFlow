using SqlFlow.Dispatch;
using SqlFlow.Dispatch.Protocol;

namespace SqlFlow.Node;

/// <summary>The control plane's own node talks to its dispatcher directly: no HTTP hop, no serialization, the same
/// protocol records. A poll still parks inside the dispatcher's long-poll, so a triggered run starts on the
/// in-process node the instant it is enqueued.</summary>
public sealed class InProcessNodeTransport : INodeTransport
{
    private readonly Dispatcher _dispatcher;

    public InProcessNodeTransport(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }

    public Task<NodePollResponse> PollAsync(NodePollRequest request, CancellationToken ct) => _dispatcher.PollAsync(request, ct);

    public Task<string?> GetFlowVersionAsync(string contentHash, CancellationToken ct)
        => _dispatcher.LoadFlowVersionAsync(contentHash, ct);

    public Task<RunContextResponse> ResolveRunContextAsync(Guid runId, RunContextRequest request, CancellationToken ct)
        => _dispatcher.ResolveRunContextAsync(runId, request, ct);

    public Task<bool> ReportTraceAsync(Guid runId, RunTraceBatch batch, CancellationToken ct)
        => _dispatcher.AppendRunTraceAsync(runId, batch, ct);

    public Task<RunOutcomeStatus> ReportRunOutcomeAsync(Guid runId, RunOutcomeRequest request, CancellationToken ct)
        => _dispatcher.RecordRunOutcomeAsync(runId, request, ct);

    public Task<bool> ReportTaskOutcomeAsync(Guid taskId, TaskOutcomeRequest request, CancellationToken ct)
        => _dispatcher.RecordTaskOutcomeAsync(taskId, request, ct);

    public Task<FanOutResponse> EnqueueFanOutAsync(Guid rootRunId, FanOutRequest request, CancellationToken ct)
        => _dispatcher.EnqueueFanOutAsync(rootRunId, request, ct);

    public Task<FanOutStateResponse> GetFanOutStateAsync(Guid rootRunId, Guid groupId, FanOutFence fence, CancellationToken ct)
        => _dispatcher.LoadFanOutStateAsync(rootRunId, groupId, fence, ct);

    public Task<FanOutCancelResponse> CancelFanOutAsync(Guid rootRunId, Guid groupId, FanOutFence fence, CancellationToken ct)
        => _dispatcher.CancelFanOutAsync(rootRunId, groupId, fence, ct);
}
