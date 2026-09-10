using SqlFlow.Core.Model;

namespace SqlFlow.Core.Abstractions;

/// <summary>
/// Storage abstraction for reading source files from anywhere - local/network disk or a cloud object
/// store. A store advertises the location schemes it handles; the engine selects one by location.
/// Async by design so cloud implementations don't block. Adding a backend (ADLS, S3, GCS) means
/// implementing this interface - no change to readers or the engine.
/// </summary>
public interface IFileStore
{
    /// <summary>True if this store handles the given location (e.g. a local/UNC path, or a URI scheme).</summary>
    bool CanHandle(string location);

    /// <summary>
    /// Lists files at a location (a single file, or a folder enumerated per <paramref name="discovery"/>). The store
    /// applies the glob, and when <see cref="FileDiscovery.Filter"/> is set it prunes out-of-window sub-directories
    /// during the walk and drops files the filter excludes, so a partitioned lake is never fully enumerated.
    /// </summary>
    Task<IReadOnlyList<FileRef>> ListAsync(string location, FileDiscovery discovery, CancellationToken ct = default);

    /// <summary>Opens a readable stream over a file.</summary>
    Task<Stream> OpenReadAsync(FileRef file, CancellationToken ct = default);
}
