namespace SqlFlow.Core.Ingestion;

/// <summary>
/// An assertion-definition store held in memory, built from a YAML document's <c>assertions:</c> block. It gives
/// without-database flows real data-quality assertions through the SAME assertion runner as full mode: the only
/// difference is where the definitions live. Contract parity with the SQL-backed store: lookups are
/// case-insensitive and an unknown name is omitted from the result (the runner skips it), never an error.
/// </summary>
public sealed class InMemoryAssertionDefinitionStore : IAssertionDefinitionStore
{
    private readonly Dictionary<string, AssertionDefinition> _definitions;

    public InMemoryAssertionDefinitionStore(IEnumerable<AssertionDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        _definitions = new Dictionary<string, AssertionDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            if (!_definitions.TryAdd(definition.Name, definition))
            {
                throw new SqlFlowException($"Duplicate assertion name '{definition.Name}'.");
            }
        }
    }

    public Task<IReadOnlyDictionary<string, AssertionDefinition>> ResolveAsync(IEnumerable<string> names, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(names);

        var resolved = new Dictionary<string, AssertionDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (_definitions.TryGetValue(name, out var definition))
            {
                resolved[name] = definition;
            }
        }

        return Task.FromResult<IReadOnlyDictionary<string, AssertionDefinition>>(resolved);
    }
}
