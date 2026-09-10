namespace SqlFlow.Core.Invoke;

/// <summary>
/// An invoke registry held entirely in memory, built from a YAML document's <c>invokes:</c> block (or any other
/// in-process source). It is what lets PreInvokeAlias/PostInvokeAlias hooks and standalone invoke runs resolve
/// WITHOUT a control database: same <see cref="DefaultInvokeRunner"/>, same dispatcher, just a dictionary behind
/// the <see cref="IInvokeFlowLoader"/> seam instead of flw.Invoke. Alias lookups are case-insensitive, matching
/// the SQL registry's collation behavior.
/// </summary>
public sealed class InMemoryInvokeFlowStore : IInvokeFlowLoader
{
    private readonly Dictionary<string, InvokeDefinition> _byAlias;

    public InMemoryInvokeFlowStore(IEnumerable<InvokeDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        _byAlias = new Dictionary<string, InvokeDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            if (!_byAlias.TryAdd(definition.InvokeAlias, definition))
            {
                throw new SqlFlowException($"Duplicate invoke name '{definition.InvokeAlias}'.");
            }
        }
    }

    public Task<InvokeDefinition> LoadByIdAsync(int flowId, CancellationToken ct = default)
    {
        var definition = _byAlias.Values.FirstOrDefault(d => d.FlowId == flowId);
        return definition is not null
            ? Task.FromResult(definition)
            : throw new SqlFlowException($"No invoke flow with FlowID {flowId} is declared.");
    }

    public Task<InvokeDefinition> LoadByAliasAsync(string invokeAlias, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invokeAlias);
        return _byAlias.TryGetValue(invokeAlias, out var definition)
            ? Task.FromResult(definition)
            : throw new SqlFlowException(
                $"No invoke named '{invokeAlias}' is declared. Declare it under 'invokes:' in the flow document.");
    }

    public Task<IReadOnlyList<InvokeDefinition>> LoadBatchAsync(string batch, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batch);
        IReadOnlyList<InvokeDefinition> flows = _byAlias.Values
            .Where(d => string.Equals(d.Batch, batch, StringComparison.OrdinalIgnoreCase) && !d.DeactivateFromBatch)
            .OrderBy(d => d.FlowId)
            .ToList();
        return Task.FromResult(flows);
    }
}
