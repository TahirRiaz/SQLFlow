using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;

namespace SqlFlow.Core.State;

/// <summary>The lightweight-mode state store: the engine has no memory. Reads return nothing; writes are no-ops.</summary>
public sealed class NullStateStore : IStateStore
{
    public Task<FlowState?> GetAsync(string flowName, CancellationToken ct = default)
        => Task.FromResult<FlowState?>(null);

    public Task SaveAsync(FlowState state, CancellationToken ct = default)
        => Task.CompletedTask;
}
