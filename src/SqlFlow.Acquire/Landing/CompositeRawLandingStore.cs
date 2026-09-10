using SqlFlow.Core;

namespace SqlFlow.Acquire.Landing;

/// <summary>Selects the raw landing store that handles a given target location (local filesystem or Azure Storage).</summary>
public sealed class CompositeRawLandingStore : IRawLandingStore
{
    private readonly IReadOnlyList<IRawLandingStore> _stores;

    public CompositeRawLandingStore(IEnumerable<IRawLandingStore> stores)
    {
        ArgumentNullException.ThrowIfNull(stores);
        _stores = stores.Where(s => s is not CompositeRawLandingStore).ToList();
    }

    public bool CanHandle(string location) => _stores.Any(s => s.CanHandle(location));

    public string Combine(string baseLocation, string relativePath) => Select(baseLocation).Combine(baseLocation, relativePath);

    public Task<bool> ExistsAsync(string location, CancellationToken ct = default) => Select(location).ExistsAsync(location, ct);

    public Task<bool> PutAsync(string location, ReadOnlyMemory<byte> content, bool overwrite, bool skipUnchanged = true, CancellationToken ct = default)
        => Select(location).PutAsync(location, content, overwrite, skipUnchanged, ct);

    public Task<IReadOnlyList<string>> ListNamesAsync(string baseLocation, CancellationToken ct = default)
        => Select(baseLocation).ListNamesAsync(baseLocation, ct);

    private IRawLandingStore Select(string location)
        => _stores.FirstOrDefault(s => s.CanHandle(location))
           ?? throw new SqlFlowException($"No landing store handles the location '{location}'. Use a local/UNC path or an Azure Storage URI (abfss:// or https://<account>.dfs.core.windows.net).");
}
