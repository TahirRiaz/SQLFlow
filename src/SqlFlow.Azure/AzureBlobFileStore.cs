using System.Collections.Concurrent;
using System.IO.Enumeration;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Model;

namespace SqlFlow.Azure;

/// <summary>
/// Azure Blob / ADLS Gen2 file store: reads source files from <c>abfss://</c>, <c>wasbs://</c>, and the
/// <c>https://&lt;account&gt;.blob|dfs.core.windows.net</c> URL forms, so file discovery and loads work against a
/// data lake exactly as they do on local disk. It authenticates through the shared
/// <see cref="IAzureCredentialFactory"/>, which honors <c>SQLFLOW_AZURE_AUTH</c> and by default chains managed
/// identity, a service principal, then Azure CLI (<c>az login</c>) - the same credential Key Vault, invoke, and
/// DuckDB cloud reads use, with no per-flow secret.
///
/// Listing uses hierarchical blob enumeration so a partitioned lake is walked one virtual directory at a time and
/// the discovery filter can prune whole out-of-window partition subtrees (<see cref="IFileDiscoveryFilter"/>)
/// before they are enumerated - the same pushdown the local store performs.
/// </summary>
public sealed class AzureBlobFileStore : IFileStore
{
    /// <summary>Bounded fan-out for the filtered hierarchical walk: sibling virtual directories list in
    /// parallel, capped so a wide lake cannot flood the service with concurrent list calls.</summary>
    private const int WalkConcurrency = 8;

    private readonly IAzureCredentialFactory _credentials;

    // One service client per storage endpoint, created lazily and kept for the store's lifetime: the client is
    // thread-safe and carries the credential, so its token cache is reused across every list and open in a run
    // (and across runs) instead of re-authenticating per call. Lazy so a racing first use builds one client.
    private readonly ConcurrentDictionary<Uri, Lazy<BlobServiceClient>> _services = new();

    public AzureBlobFileStore(IAzureCredentialFactory credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        _credentials = credentials;
    }

    public bool CanHandle(string location) => AzureBlobLocation.IsAzureStorageUri(location);

    public async Task<IReadOnlyList<FileRef>> ListAsync(string location, FileDiscovery discovery, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        var loc = AzureBlobLocation.Parse(location);
        var pattern = string.IsNullOrWhiteSpace(discovery.Pattern) ? "*" : discovery.Pattern;
        var filter = discovery.Filter;

        try
        {
            var container = ContainerClient(loc);

            // A location that points straight at a blob is a single file (mirrors the local store's File.Exists path).
            // A trailing slash is an explicit "this is a directory" and never a single file.
            if (loc.BlobPath.Length > 0 && !loc.BlobPath.EndsWith('/'))
            {
                var blob = container.GetBlobClient(loc.BlobPath);
                try
                {
                    var props = await blob.GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false);
                    // On an ADLS Gen2 (hierarchical-namespace) account a directory exists as a zero-length blob
                    // carrying 'hdi_isfolder=true'. It is a folder, not a file: fall through to the walk below so the
                    // glob and recursion still apply, otherwise the location would match one empty "file" and read
                    // nothing.
                    if (!IsDirectoryMarker(props.Value.Metadata))
                    {
                        var single = ToRef(loc, loc.BlobPath, props.Value.ContentLength, props.Value.LastModified);
                        return filter is null || filter.Includes(single) ? [single] : [];
                    }
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    // Not a blob: treat the path as a virtual directory prefix and enumerate under it below.
                }
            }

            var prefix = loc.BlobPath.Length == 0 ? string.Empty : loc.BlobPath.TrimEnd('/') + "/";
            var results = new List<FileRef>();
            if (discovery.Recursive && filter is null)
            {
                // No directory-level pruning is needed, so a flat listing under the prefix is one paginated pass
                // instead of one hierarchical round-trip per virtual folder - decisive on a date-partitioned lake
                // (YYYY/MM/DD) where the hierarchical walk would issue thousands of sequential list calls.
                await FlatListAsync(container, loc, prefix, pattern, results, ct).ConfigureAwait(false);
            }
            else
            {
                // A filter can prune whole out-of-window subtrees, so the hierarchical walk (which can decline to
                // enter a directory) is worth its per-folder round-trip; the non-recursive case also needs the
                // delimiter so it stops at one level.
                await WalkAsync(container, loc, prefix, pattern, discovery.Recursive, filter, results, ct).ConfigureAwait(false);
            }

            results.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));
            return results;
        }
        catch (Exception ex) when (ex is not SqlFlowException and not OperationCanceledException)
        {
            throw Translate(loc, ex);
        }
    }

    public async Task<Stream> OpenReadAsync(FileRef file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        var loc = AzureBlobLocation.Parse(file.Path);
        try
        {
            var blob = ContainerClient(loc).GetBlobClient(loc.BlobPath);
            var response = await blob.DownloadStreamingAsync(cancellationToken: ct).ConfigureAwait(false);
            return response.Value.Content;
        }
        catch (Exception ex) when (ex is not SqlFlowException and not OperationCanceledException)
        {
            throw Translate(loc, ex);
        }
    }

    /// <summary>
    /// Turns an Azure SDK failure into an actionable <see cref="SqlFlowException"/>: an auth failure points at
    /// <c>az login</c> / the service-principal variables, a request failure names the account, container, and HTTP
    /// status, and anything else carries the account, container, and underlying cause. The retry wrapper's
    /// <see cref="AggregateException"/> is unwrapped to the informative inner cause.
    /// </summary>
    private static SqlFlowException Translate(AzureBlobLocation loc, Exception ex)
    {
        var cause = Unwrap(ex);
        return cause switch
        {
            AuthenticationFailedException or CredentialUnavailableException => new SqlFlowException(
                $"Azure authentication failed for storage account '{loc.Account}'. Sign in with 'az login', or set "
                + "SQLFLOW_AZURE_AUTH and the AZURE_* variables for a service principal or managed identity "
                + $"({cause.Message}).", ex),
            RequestFailedException rfe => new SqlFlowException(
                $"Azure Storage request failed for '{loc.Account}/{loc.Container}' (status {rfe.Status}): {rfe.Message}", ex),
            _ => new SqlFlowException(
                $"Could not access Azure Storage location '{loc.Account}/{loc.Container}': {cause.Message}", ex),
        };
    }

    private static Exception Unwrap(Exception ex)
        => ex is AggregateException { InnerException: { } inner } ? Unwrap(inner) : ex;

    /// <summary>
    /// Enumerates one virtual-directory level under <paramref name="prefix"/>: blobs whose name matches the glob
    /// are added (after the per-file filter), and each sub-prefix is descended only when recursing and the filter
    /// admits it - so an out-of-window partition folder is skipped without being listed. Sibling sub-directories
    /// are independent list round trips, so the descent fans out with bounded concurrency
    /// (<see cref="WalkConcurrency"/>); adds are serialized on the sink, and the caller sorts the aggregate, so
    /// the result is the same deterministically ordered set the sequential walk produced.
    /// </summary>
    private static async Task WalkAsync(
        BlobContainerClient container,
        AzureBlobLocation loc,
        string prefix,
        string pattern,
        bool recursive,
        IFileDiscoveryFilter? filter,
        List<FileRef> sink,
        CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(WalkConcurrency, WalkConcurrency);
        await WalkDirectoryAsync(container, loc, prefix, pattern, recursive, filter, sink, gate, ct).ConfigureAwait(false);
    }

    private static async Task WalkDirectoryAsync(
        BlobContainerClient container,
        AzureBlobLocation loc,
        string prefix,
        string pattern,
        bool recursive,
        IFileDiscoveryFilter? filter,
        List<FileRef> sink,
        SemaphoreSlim gate,
        CancellationToken ct)
    {
        // Sub-prefixes to descend are collected during enumeration and walked afterwards, so the page enumerator is
        // not held open across the recursive listing calls. The concurrency slot is held only while this level
        // enumerates and is released before the children run: a parent awaiting its children holds no slot, so a
        // tree deeper than the slot count cannot deadlock the pool.
        var subDirectories = new List<string>();
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var pageable = container.GetBlobsByHierarchyAsync(delimiter: "/", prefix: prefix, cancellationToken: ct);
            await foreach (var entry in pageable.ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();

                if (entry.IsBlob)
                {
                    var name = LastSegment(entry.Blob.Name);
                    // A hierarchical-namespace directory can surface as a zero-length blob whose name ends in '/'; it
                    // has no file name, so the glob match below also excludes it.
                    if (name.Length == 0 || !FileSystemName.MatchesSimpleExpression(pattern, name))
                    {
                        continue;
                    }

                    var reference = ToRef(
                        loc,
                        entry.Blob.Name,
                        entry.Blob.Properties.ContentLength ?? 0,
                        entry.Blob.Properties.LastModified);

                    if (filter is null || filter.Includes(reference))
                    {
                        lock (sink)
                        {
                            sink.Add(reference);
                        }
                    }
                }
                else if (recursive && entry.IsPrefix
                    && (filter is null || filter.ShouldEnterDirectory(loc.UriFor(entry.Prefix))))
                {
                    subDirectories.Add(entry.Prefix);
                }
            }
        }
        finally
        {
            gate.Release();
        }

        if (subDirectories.Count == 0)
        {
            return;
        }

        if (subDirectories.Count == 1)
        {
            await WalkDirectoryAsync(container, loc, subDirectories[0], pattern, recursive, filter, sink, gate, ct).ConfigureAwait(false);
            return;
        }

        // A per-directory failure fails the whole walk (Task.WhenAll surfaces it once every sibling settles),
        // preserving the sequential walk's all-or-nothing contract: no directory is ever silently skipped.
        await Task.WhenAll(subDirectories.Select(
            subDirectory => WalkDirectoryAsync(container, loc, subDirectory, pattern, recursive, filter, sink, gate, ct))).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists every blob under <paramref name="prefix"/> in a single flat, paginated enumeration (no delimiter),
    /// keeping those whose file name matches the glob. Used when recursing with no directory-level filter, where a
    /// flat scan is equivalent to the hierarchical walk but avoids a round-trip per virtual folder. ADLS Gen2
    /// directory-marker blobs have no <c>.ext</c> file name, so the glob excludes them.
    /// </summary>
    private static async Task FlatListAsync(
        BlobContainerClient container,
        AzureBlobLocation loc,
        string prefix,
        string pattern,
        List<FileRef> sink,
        CancellationToken ct)
    {
        var pageable = container.GetBlobsAsync(prefix: prefix, cancellationToken: ct);
        await foreach (var blob in pageable.ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            var name = LastSegment(blob.Name);
            if (name.Length == 0 || !FileSystemName.MatchesSimpleExpression(pattern, name))
            {
                continue;
            }

            sink.Add(ToRef(loc, blob.Name, blob.Properties.ContentLength ?? 0, blob.Properties.LastModified));
        }
    }

    private BlobContainerClient ContainerClient(AzureBlobLocation loc)
    {
        var service = _services.GetOrAdd(
            loc.BlobServiceEndpoint,
            endpoint => new Lazy<BlobServiceClient>(() => new BlobServiceClient(endpoint, _credentials.Create()))).Value;
        return service.GetBlobContainerClient(loc.Container);
    }

    private static FileRef ToRef(AzureBlobLocation loc, string blobName, long size, DateTimeOffset? modified) => new()
    {
        Path = loc.UriFor(blobName),
        Name = LastSegment(blobName),
        Size = size,
        Modified = modified,
    };

    /// <summary>
    /// True when a blob's metadata marks it an ADLS Gen2 directory (<c>hdi_isfolder=true</c>): a zero-length
    /// placeholder for a virtual folder, which must be walked as a prefix rather than read as a file.
    /// </summary>
    private static bool IsDirectoryMarker(IDictionary<string, string> metadata)
        => metadata.TryGetValue("hdi_isfolder", out var flag)
            && string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);

    private static string LastSegment(string blobName)
    {
        var trimmed = blobName.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }
}
