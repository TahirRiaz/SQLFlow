namespace SqlFlow.Azure.Storage;

/// <summary>
/// One container reached over one endpoint. <see cref="AzureStorageContainer"/> holds one per endpoint and routes every
/// operation to the endpoint the account is reached on; the two implementations give the same results, so a caller never
/// knows which one served it. Paths are in-container paths without a leading slash; a listing prefix is empty (the
/// container root) or ends with a slash.
/// </summary>
internal interface IAzureStorageBackend
{
    AzureStorageEndpoint Endpoint { get; }

    /// <summary>Sends one cheap request to the container (its properties) and lets any failure surface unclassified:
    /// the selector reads it for whether the endpoint can be reached at all. Never retried, and bounded by the probe's
    /// network timeout.</summary>
    Task ProbeAsync(CancellationToken ct);

    /// <summary>The files and directories under <paramref name="prefix"/>: every level when <paramref name="recursive"/>,
    /// otherwise the files and directories directly under it. A prefix that names no directory lists nothing; a missing
    /// container fails with status 404.</summary>
    IAsyncEnumerable<AzureStorageItem> ListAsync(string prefix, bool recursive, CancellationToken ct);

    /// <summary>The path's properties, or <c>null</c> when nothing exists there (or its container does not).</summary>
    Task<AzureStorageProperties?> GetPropertiesAsync(string path, CancellationToken ct);

    /// <summary>A forward-only stream over the whole file.</summary>
    Task<Stream> OpenReadAsync(string path, CancellationToken ct);

    /// <summary>A forward-only stream over <paramref name="length"/> bytes from <paramref name="offset"/>.</summary>
    Task<Stream> OpenReadAsync(string path, long offset, long length, CancellationToken ct);

    /// <summary>A seekable stream that fetches ranges on demand and fails if the file changes while it is read.</summary>
    Task<Stream> OpenSeekableReadAsync(string path, int? bufferSize, CancellationToken ct);

    /// <summary>Writes the whole file from <paramref name="content"/>, stamping <paramref name="contentHash"/> as its
    /// stored MD5 when given. Without <paramref name="overwrite"/> it fails with status 409 when the path exists.</summary>
    Task UploadAsync(string path, Stream content, bool overwrite, byte[]? contentHash, CancellationToken ct);

    /// <summary>Creates or truncates the file and returns a stream whose bytes are committed on flush and dispose.</summary>
    Task<Stream> OpenWriteAsync(string path, int? bufferSize, CancellationToken ct);

    /// <summary>Deletes the file; false when nothing was there.</summary>
    Task<bool> DeleteIfExistsAsync(string path, CancellationToken ct);

    /// <summary>Creates the container when it does not exist.</summary>
    Task CreateContainerIfNotExistsAsync(CancellationToken ct);
}
