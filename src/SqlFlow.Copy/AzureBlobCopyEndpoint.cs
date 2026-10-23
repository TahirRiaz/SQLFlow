using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using Azure;
using SqlFlow.Azure;
using SqlFlow.Azure.Storage;
using SqlFlow.Core;
using SqlFlow.Core.Copy;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Copy;

/// <summary>
/// The Azure Blob / ADLS Gen2 endpoint, for both the ADLS authority form (<c>abfss://fs@account.dfs…/path</c>) and
/// the https URL form. Its containers come from <see cref="AzureStorageAccess"/>, so the account is reached over its Blob
/// or its Data Lake endpoint, whichever this host can reach. It authenticates with the ambient managed identity / az login
/// by default (the same credential Key Vault, invoke, and the acquisition landing store use, so no per-flow storage
/// secret is needed), or with a connection string / SAS token / account key when the endpoint declares a secret
/// reference (a vendor drop zone that only issues a key). An endpoint's secret is resolved once and its container reused
/// across the run's reads and writes.
/// </summary>
public sealed class AzureBlobCopyEndpoint : ICopyEndpoint
{
    /// <summary>How many listed files' stored hashes are read at once when the listing does not carry them (the Data
    /// Lake endpoint's does not): one properties request per file, in bounded parallel batches.</summary>
    private const int HashBatch = 32;

    private readonly AzureStorageAccess _storage;
    private readonly ISecretResolver _secrets;
    private readonly ConcurrentDictionary<string, Lazy<Task<AzureStorageContainer>>> _containers = new(StringComparer.Ordinal);

    public AzureBlobCopyEndpoint(AzureStorageAccess storage, ISecretResolver secrets)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(secrets);
        _storage = storage;
        _secrets = secrets;
    }

    public static bool IsAzure(string location) => AzureBlobLocation.IsAzureStorageUri(location);

    public bool CanHandle(string location) => IsAzure(location);

    public async IAsyncEnumerable<CopyItem> ListAsync(
        CopyEndpoint endpoint, CopyModifiedWindow window, [EnumeratorCancellation] CancellationToken ct)
    {
        var loc = AzureBlobLocation.Parse(endpoint.Location);
        var container = await ContainerAsync(endpoint, loc, ct).ConfigureAwait(false);
        var prefix = loc.BlobPath.Length == 0 ? string.Empty : loc.BlobPath.TrimEnd('/') + "/";
        var unhashed = new List<AzureStorageItem>(HashBatch);

        await foreach (var item in container.ListAsync(prefix, recursive: true, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (item.IsDirectory)
            {
                continue; // a directory, or a virtual directory marker
            }

            var name = item.Name;
            var leaf = name[(name.LastIndexOf('/') + 1)..];
            if (leaf.Length == 0)
            {
                continue;
            }

            // 'recursive: false' keeps only blobs directly under the prefix (no further '/').
            if (!endpoint.Recursive && name[prefix.Length..].Contains('/', StringComparison.Ordinal))
            {
                continue;
            }

            if (!FileSystemName.MatchesSimpleExpression(endpoint.Pattern, leaf) || !window.Includes(item.LastModified))
            {
                continue;
            }

            // ContentHash comes back on a Blob listing for free: the engine compares it against the target's hash to
            // skip an unchanged blob without ever downloading it. A Data Lake listing carries none, so the stored hash
            // of each matched file is read from its properties instead, which is still no download.
            if (item.ContentHashListed)
            {
                yield return ToItem(prefix, item, item.ContentHash);
                continue;
            }

            unhashed.Add(item);
            if (unhashed.Count == HashBatch)
            {
                foreach (var hashed in await WithStoredHashesAsync(container, prefix, unhashed, ct).ConfigureAwait(false))
                {
                    yield return hashed;
                }

                unhashed.Clear();
            }
        }

        foreach (var hashed in await WithStoredHashesAsync(container, prefix, unhashed, ct).ConfigureAwait(false))
        {
            yield return hashed;
        }
    }

    public async Task<byte[]> ReadAsync(CopyEndpoint endpoint, string absolutePath, CancellationToken ct)
    {
        var loc = AzureBlobLocation.Parse(endpoint.Location);
        var container = await ContainerAsync(endpoint, loc, ct).ConfigureAwait(false);
        try
        {
            return await container.ReadAllBytesAsync(absolutePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not SqlFlowException and not OperationCanceledException)
        {
            throw Translate(endpoint.Location, ex);
        }
    }

    public async Task<IReadOnlyDictionary<string, byte[]?>> TargetHashIndexAsync(CopyEndpoint endpoint, CancellationToken ct)
    {
        var loc = AzureBlobLocation.Parse(endpoint.Location);
        var container = await ContainerAsync(endpoint, loc, ct).ConfigureAwait(false);
        var prefix = loc.BlobPath.Length == 0 ? string.Empty : loc.BlobPath.TrimEnd('/') + "/";
        var index = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        try
        {
            // One listing returns every target file. Over the Blob endpoint it carries each one's ContentHash (MD5), so
            // the engine compares the whole set in memory instead of a metadata call per file; over the Data Lake
            // endpoint the hash is unknown (null) and the engine reads it per file, only for the files a source matched.
            await foreach (var item in container.ListAsync(prefix, recursive: true, ct).ConfigureAwait(false))
            {
                if (item.IsDirectory)
                {
                    continue; // a directory, or a virtual directory marker
                }

                var name = item.Name;
                var leaf = name[(name.LastIndexOf('/') + 1)..];
                if (leaf.Length == 0)
                {
                    continue;
                }

                index[name[prefix.Length..]] = item.ContentHash;
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // The target container/prefix does not exist yet: nothing landed, so every file is new.
            return index;
        }
        catch (Exception ex) when (ex is not SqlFlowException and not OperationCanceledException)
        {
            throw Translate(endpoint.Location, ex);
        }

        return index;
    }

    public async Task<byte[]?> TargetContentHashAsync(CopyEndpoint endpoint, string relativePath, CancellationToken ct)
    {
        var loc = AzureBlobLocation.Parse(endpoint.Location);
        var blobName = BlobName(loc, relativePath);
        var container = await ContainerAsync(endpoint, loc, ct).ConfigureAwait(false);
        try
        {
            return (await container.GetPropertiesAsync(blobName, ct).ConfigureAwait(false))?.ContentHash;
        }
        catch (Exception ex) when (ex is not SqlFlowException and not OperationCanceledException)
        {
            throw Translate(endpoint.Location, ex);
        }
    }

    public async Task<string> WriteAsync(
        CopyEndpoint endpoint, string relativePath, ReadOnlyMemory<byte> content, bool overwrite, byte[] contentHash, CancellationToken ct)
    {
        var loc = AzureBlobLocation.Parse(endpoint.Location);
        var blobName = BlobName(loc, relativePath);
        var container = await ContainerAsync(endpoint, loc, ct).ConfigureAwait(false);
        try
        {
            // Stamp ContentHash (the MD5 the engine computed) so the next run can compare byte-identity from the file's
            // stored properties alone, through TargetContentHashAsync, and skip an unchanged file without downloading it.
            using var stream = new MemoryStream(content.ToArray(), writable: false);
            await container.UploadAsync(blobName, stream, overwrite, contentHash, ct).ConfigureAwait(false);
            return loc.UriFor(blobName);
        }
        catch (RequestFailedException ex) when (!overwrite && ex.Status == 409)
        {
            throw new SqlFlowException($"Copy target blob '{loc.UriFor(blobName)}' already exists and overwrite is disabled.", ex);
        }
        catch (Exception ex) when (ex is not SqlFlowException and not OperationCanceledException)
        {
            throw Translate(endpoint.Location, ex);
        }
    }

    private static CopyItem ToItem(string prefix, AzureStorageItem item, byte[]? hash)
    {
        var name = item.Name;
        return new CopyItem(name, name[prefix.Length..], name[(name.LastIndexOf('/') + 1)..], item.LastModified, item.Length, hash);
    }

    /// <summary>The listed files with the content hash each one stores, read concurrently from their properties. A file
    /// gone between the listing and its properties gets no hash, and the engine then reads it (and reports it missing).</summary>
    private static async Task<IReadOnlyList<CopyItem>> WithStoredHashesAsync(
        AzureStorageContainer container, string prefix, List<AzureStorageItem> items, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var properties = await Task.WhenAll(items.Select(item => container.GetPropertiesAsync(item.Name, ct))).ConfigureAwait(false);
        return items.Select((item, i) => ToItem(prefix, item, properties[i]?.ContentHash)).ToList();
    }

    private static string BlobName(AzureBlobLocation loc, string relativePath)
    {
        CopyTargetPath.RequireContained(relativePath);
        var basePath = loc.BlobPath.TrimEnd('/');
        return (basePath.Length == 0 ? relativePath : $"{basePath}/{relativePath}").TrimStart('/');
    }

    /// <summary>
    /// The endpoint's container, its secret resolved once per account, container and secret reference. A failed
    /// resolution is not kept, so the next call resolves again; a caller's cancellation cancels its own wait only.
    /// </summary>
    private async Task<AzureStorageContainer> ContainerAsync(CopyEndpoint endpoint, AzureBlobLocation loc, CancellationToken ct)
    {
        var key = string.Join('|', loc.Account.ToLowerInvariant(), loc.Container.ToLowerInvariant(),
            endpoint.ConnectionStringRef, endpoint.AccountKeyRef, endpoint.SasTokenRef);
        var lazy = _containers.GetOrAdd(key, _ => new Lazy<Task<AzureStorageContainer>>(() => BuildContainerAsync(endpoint, loc)));
        try
        {
            return await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        catch when (lazy.Value.IsFaulted || lazy.Value.IsCanceled)
        {
            _containers.TryRemove(new KeyValuePair<string, Lazy<Task<AzureStorageContainer>>>(key, lazy));
            throw;
        }
    }

    private async Task<AzureStorageContainer> BuildContainerAsync(CopyEndpoint endpoint, AzureBlobLocation loc)
    {
        AzureStorageAuth auth;
        if (!string.IsNullOrWhiteSpace(endpoint.ConnectionStringRef))
        {
            auth = AzureStorageAuth.FromConnectionString(
                await _secrets.ResolveAsync(endpoint.ConnectionStringRef!, CancellationToken.None).ConfigureAwait(false));
        }
        else if (!string.IsNullOrWhiteSpace(endpoint.AccountKeyRef))
        {
            auth = AzureStorageAuth.FromAccountKey(
                await _secrets.ResolveAsync(endpoint.AccountKeyRef!, CancellationToken.None).ConfigureAwait(false));
        }
        else if (!string.IsNullOrWhiteSpace(endpoint.SasTokenRef))
        {
            auth = AzureStorageAuth.FromSharedAccessSignature(
                await _secrets.ResolveAsync(endpoint.SasTokenRef!, CancellationToken.None).ConfigureAwait(false));
        }
        else
        {
            auth = AzureStorageAuth.Ambient;
        }

        return _storage.Container(loc, auth);
    }

    private static SqlFlowException Translate(string location, Exception ex)
    {
        var cause = ex is AggregateException { InnerException: { } inner } ? inner : ex;
        var over = AzureStorageFailures.EndpointOf(ex) is { } used ? $" over the {used.HostLabel()} endpoint" : string.Empty;
        return cause switch
        {
            RequestFailedException rfe => new SqlFlowException(
                $"Azure Storage request failed for copy endpoint '{location}'{over} (status {rfe.Status}): {rfe.Message}", ex),
            _ => new SqlFlowException($"Could not access Azure Storage copy endpoint '{location}': {cause.Message}", ex),
        };
    }
}
