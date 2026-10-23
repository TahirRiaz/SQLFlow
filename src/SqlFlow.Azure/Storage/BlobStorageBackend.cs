using System.Runtime.CompilerServices;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace SqlFlow.Azure.Storage;

/// <summary>
/// A container over the Blob service endpoint, through the Azure Blob SDK: the calls the stores made before the storage
/// layer chose an endpoint, unchanged. A directory is recognised the way Azure reports it over this endpoint: a virtual
/// folder (a listing prefix, or a zero-length blob whose name ends in a slash) and, on a hierarchical-namespace account,
/// the zero-length blob carrying the <c>hdi_isfolder</c> metadata marker that ADLS Gen2 keeps for every directory.
/// </summary>
internal sealed class BlobStorageBackend : IAzureStorageBackend
{
    private const string DirectoryMarker = "hdi_isfolder";

    private readonly BlobContainerClient _container;
    private readonly BlobContainerClient _probe;

    public BlobStorageBackend(BlobContainerClient container, BlobContainerClient probe)
    {
        _container = container;
        _probe = probe;
    }

    public AzureStorageEndpoint Endpoint => AzureStorageEndpoint.Blob;

    public async Task ProbeAsync(CancellationToken ct)
        => await _probe.GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false);

    public async IAsyncEnumerable<AzureStorageItem> ListAsync(string prefix, bool recursive, [EnumeratorCancellation] CancellationToken ct)
    {
        var listed = prefix.TrimEnd('/');
        var blobPrefix = prefix.Length == 0 ? null : prefix;

        if (recursive)
        {
            await foreach (var blob in _container
                .GetBlobsAsync(new GetBlobsOptions { Prefix = blobPrefix, Traits = BlobTraits.Metadata }, ct)
                .ConfigureAwait(false))
            {
                var name = blob.Name.TrimEnd('/');
                // A folder placeholder for the listed folder itself is not an entry under it.
                if (name.Length == 0 || name == listed)
                {
                    continue;
                }

                yield return Item(blob.Name, blob.Properties, blob.Metadata);
            }

            yield break;
        }

        // A hierarchical-namespace directory can surface twice at one level: as the prefix its children share and as its
        // own marker blob. It is one directory, listed once.
        var directories = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var entry in _container
            .GetBlobsByHierarchyAsync(new GetBlobsByHierarchyOptions { Delimiter = "/", Prefix = blobPrefix, Traits = BlobTraits.Metadata }, ct)
            .ConfigureAwait(false))
        {
            if (entry.IsPrefix)
            {
                var directory = entry.Prefix.TrimEnd('/');
                if (directory.Length > 0 && directories.Add(directory))
                {
                    yield return Directory(directory, null);
                }

                continue;
            }

            var name = entry.Blob.Name.TrimEnd('/');
            if (name.Length == 0 || name == listed)
            {
                continue;
            }

            var item = Item(entry.Blob.Name, entry.Blob.Properties, entry.Blob.Metadata);
            if (!item.IsDirectory || directories.Add(item.Name))
            {
                yield return item;
            }
        }
    }

    public async Task<AzureStorageProperties?> GetPropertiesAsync(string path, CancellationToken ct)
    {
        try
        {
            var properties = (await _container.GetBlobClient(path).GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false)).Value;
            return new AzureStorageProperties(
                properties.ContentLength,
                properties.LastModified,
                IsDirectoryMarker(properties.Metadata) || path.EndsWith('/'),
                properties.ContentHash is { Length: > 0 } hash ? hash : null,
                properties.ETag.ToString());
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct)
        => (await _container.GetBlobClient(path).DownloadStreamingAsync(cancellationToken: ct).ConfigureAwait(false)).Value.Content;

    public async Task<Stream> OpenReadAsync(string path, long offset, long length, CancellationToken ct)
        => (await _container.GetBlobClient(path)
            .DownloadStreamingAsync(new BlobDownloadOptions { Range = new HttpRange(offset, length) }, ct)
            .ConfigureAwait(false)).Value.Content;

    public Task<Stream> OpenSeekableReadAsync(string path, int? bufferSize, CancellationToken ct)
        => _container.GetBlobClient(path).OpenReadAsync(new BlobOpenReadOptions(allowModifications: false) { BufferSize = bufferSize }, ct);

    public async Task UploadAsync(string path, Stream content, bool overwrite, byte[]? contentHash, CancellationToken ct)
    {
        var options = new BlobUploadOptions();
        if (contentHash is not null)
        {
            options.HttpHeaders = new BlobHttpHeaders { ContentHash = contentHash };
        }

        if (!overwrite)
        {
            // Fail (rather than clobber) when the blob already exists: the service answers 409.
            options.Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All };
        }

        await _container.GetBlobClient(path).UploadAsync(content, options, ct).ConfigureAwait(false);
    }

    public Task<Stream> OpenWriteAsync(string path, int? bufferSize, CancellationToken ct)
        => _container.GetBlobClient(path).OpenWriteAsync(
            overwrite: true,
            bufferSize is { } size ? new BlobOpenWriteOptions { BufferSize = size } : null,
            ct);

    public async Task<bool> DeleteIfExistsAsync(string path, CancellationToken ct)
        => (await _container.GetBlobClient(path).DeleteIfExistsAsync(cancellationToken: ct).ConfigureAwait(false)).Value;

    public async Task CreateContainerIfNotExistsAsync(CancellationToken ct)
        => await _container.CreateIfNotExistsAsync(cancellationToken: ct).ConfigureAwait(false);

    private static AzureStorageItem Item(string blobName, BlobItemProperties properties, IDictionary<string, string>? metadata)
    {
        var name = blobName.TrimEnd('/');
        if (blobName.EndsWith('/') || IsDirectoryMarker(metadata))
        {
            return Directory(name, properties.LastModified);
        }

        return new AzureStorageItem(
            name,
            IsDirectory: false,
            properties.ContentLength ?? 0,
            properties.LastModified,
            properties.ContentHash is { Length: > 0 } hash ? hash : null,
            ContentHashListed: true);
    }

    private static AzureStorageItem Directory(string name, DateTimeOffset? modified)
        => new(name, IsDirectory: true, 0, modified, null, ContentHashListed: true);

    private static bool IsDirectoryMarker(IDictionary<string, string>? metadata)
        => metadata is not null
           && metadata.TryGetValue(DirectoryMarker, out var flag)
           && string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);
}
