using SqlFlow.Core.Model;

namespace SqlFlow.Core.Abstractions;

/// <summary>
/// Stores per-flow memory. The default <c>NullStateStore</c> implements lightweight mode (no memory);
/// full mode plugs in a database-backed implementation.
/// </summary>
public interface IStateStore
{
    Task<FlowState?> GetAsync(string flowName, CancellationToken ct = default);

    Task SaveAsync(FlowState state, CancellationToken ct = default);
}
