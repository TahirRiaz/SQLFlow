namespace SqlFlow.Core.Connections;

/// <summary>
/// An alias registry held entirely in memory, built from a YAML document's <c>connections:</c> block (or any
/// other in-process source). It is what lets the relational flow model's <c>@alias</c> connection references
/// resolve WITHOUT a control database: same resolver, same canonicalizer, same policy gates, just a dictionary
/// behind the seam instead of flw.DataSource. Registered sources default to
/// <see cref="CredentialMode.InlineConnectionString"/>, the trust level of a reference the author placed in the
/// document themselves (typically a whole <c>${env:...}</c> / <c>${keyvault:...}</c> reference; the secretless
/// rule that no secret value rests in the file still holds).
/// </summary>
public sealed class InMemoryDataSourceStore : IDataSourceStore
{
    private readonly Dictionary<string, DataSource> _sources;

    public InMemoryDataSourceStore(IEnumerable<DataSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = new Dictionary<string, DataSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            if (!_sources.TryAdd(source.Alias, source))
            {
                throw new SqlFlowException($"Duplicate connection name '{source.Alias}'.");
            }
        }
    }

    /// <summary>Builds a store from name/reference pairs, each becoming an MSSQL source whose reference is
    /// trusted as authored (CredentialMode.InlineConnectionString).</summary>
    public static InMemoryDataSourceStore FromReferences(IEnumerable<KeyValuePair<string, string>> references)
    {
        ArgumentNullException.ThrowIfNull(references);
        return new InMemoryDataSourceStore(references.Select(pair => new DataSource
        {
            Alias = pair.Key,
            Kind = DataSourceKind.MSSQL,
            ConnectionRef = pair.Value,
            Credential = new CredentialProfile { Mode = CredentialMode.InlineConnectionString },
        }));
    }

    public bool SupportsAliases => true;

    public Task<DataSource> ResolveAsync(string aliasName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aliasName);
        return _sources.TryGetValue(aliasName, out var source)
            ? Task.FromResult(source)
            : throw new SqlFlowException(
                $"No connection named '{aliasName}' is declared. Declare it under 'connections:' in the flow document, or use 'connection:' with an inline reference.");
    }
}
