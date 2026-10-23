using System.IO.Enumeration;
using Azure;
using Azure.Identity;
using SqlFlow.Azure.Storage;
using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Model;

namespace SqlFlow.Azure;

/// <summary>
/// Azure Blob / ADLS Gen2 file store: reads source files from <c>abfss://</c>, <c>wasbs://</c>, and the
/// <c>https://&lt;account&gt;.blob|dfs.core.windows.net</c> URL forms, so file discovery and loads work against a
/// data lake exactly as they do on local disk. It goes through <see cref="AzureStorageAccess"/>, so the account is
/// reached over its Blob or its Data Lake endpoint, whichever this host can reach, and authenticates with the shared
/// <see cref="IAzureCredentialFactory"/>, which honors <c>SQLFLOW_AZURE_AUTH</c> and by default chains managed
/// identity, a service principal, then Azure CLI (<c>az login</c>) - the same credential Key Vault, invoke, and
/// DuckDB cloud reads use, with no per-flow secret.
///
/// Listing walks the lake one directory at a time so the discovery filter can prune whole out-of-window partition
/// subtrees (<see cref="IFileDiscoveryFilter"/>) before they are enumerated - the same pushdown the local store
/// performs.
/// </summary>
public sealed class AzureBlobFileStore : IFileStore
{
    /// <summary>Bounded fan-out for the filtered hierarchical walk: sibling virtual directories list in
    /// parallel, capped so a wide lake cannot flood the service with concurrent list calls.</summary>
    private const int WalkConcurrency = 8;

    private readonly AzureStorageAccess _storage;

    public AzureBlobFileStore(AzureStorageAccess storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
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
            var container = _storage.Container(loc);

            // A location that points straight at a blob is a single file (mirrors the local store's File.Exists path).
            // A trailing slash is an explicit "this is a directory" and never a single file.
            if (loc.BlobPath.Length > 0 && !loc.BlobPath.EndsWith('/'))
            {
                // Nothing there means the path is a virtual directory prefix, enumerated below. On an ADLS Gen2
                // (hierarchical-namespace) account a directory is a path of its own: a folder, not a file, so it falls
                // through to the walk below as well and the glob and recursion still apply, otherwise the location
                // would match one empty "file" and read nothing.
                var props = await container.GetPropertiesAsync(loc.BlobPath, ct).ConfigureAwait(false);
                if (props is { IsDirectory: false })
                {
                    var single = ToRef(loc, loc.BlobPath, props.Length, props.LastModified);
                    return filter is null || filter.Includes(single) ? [single] : [];
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
                // one-level listing so it stops at one level.
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
            return await _storage.Container(loc).OpenReadAsync(loc.BlobPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not SqlFlowException and not OperationCanceledException)
        {
            throw Translate(loc, ex);
        }
    }

    /// <summary>
    /// Turns an Azure SDK failure into an actionable <see cref="SqlFlowException"/>: an auth failure points at
    /// <c>az login</c> / the service-principal variables, a request failure names the account, container, the
    /// endpoint it went to, and HTTP status, and anything else carries the account, container, and underlying cause.
    /// The retry wrapper's <see cref="AggregateException"/> is unwrapped to the informative inner cause.
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
                $"Azure Storage request failed for '{loc.Account}/{loc.Container}'{Over(ex)} (status {rfe.Status}): {rfe.Message}", ex),
            _ => new SqlFlowException(
                $"Could not access Azure Storage location '{loc.Account}/{loc.Container}': {cause.Message}", ex),
        };
    }

    private static Exception Unwrap(Exception ex)
        => ex is AggregateException { InnerException: { } inner } ? Unwrap(inner) : ex;

    /// <summary>" over the dfs endpoint" (or blob) when the storage layer recorded where the failed request went.</summary>
    private static string Over(Exception ex)
        => AzureStorageFailures.EndpointOf(ex) is { } endpoint ? $" over the {endpoint.HostLabel()} endpoint" : string.Empty;

    /// <summary>
    /// Enumerates one directory level under <paramref name="prefix"/>: files whose name matches the glob are added
    /// (after the per-file filter), and each subdirectory is descended only when recursing and the filter admits it -
    /// so an out-of-window partition folder is skipped without being listed. Sibling subdirectories are independent
    /// list round trips, so the descent fans out with bounded concurrency (<see cref="WalkConcurrency"/>); adds are
    /// serialized on the sink, and the caller sorts the aggregate, so the result is the same deterministically ordered
    /// set the sequential walk produced.
    /// </summary>
    private static async Task WalkAsync(
        AzureStorageContainer container,
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
        AzureStorageContainer container,
        AzureBlobLocation loc,
        string prefix,
        string pattern,
        bool recursive,
        IFileDiscoveryFilter? filter,
        List<FileRef> sink,
        SemaphoreSlim gate,
        CancellationToken ct)
    {
        // Subdirectories to descend are collected during enumeration and walked afterwards, so the page enumerator is
        // not held open across the recursive listing calls. The concurrency slot is held only while this level
        // enumerates and is released before the children run: a parent awaiting its children holds no slot, so a
        // tree deeper than the slot count cannot deadlock the pool.
        var subDirectories = new List<string>();
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await foreach (var entry in container.ListAsync(prefix, recursive: false, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();

                if (entry.IsDirectory)
                {
                    // A directory is descended as its prefix; the trailing slash marks it a folder for the filter.
                    var directory = entry.Name + "/";
                    if (recursive && (filter is null || filter.ShouldEnterDirectory(loc.UriFor(directory))))
                    {
                        subDirectories.Add(directory);
                    }

                    continue;
                }

                var name = LastSegment(entry.Name);
                if (name.Length == 0 || !FileSystemName.MatchesSimpleExpression(pattern, name))
                {
                    continue;
                }

                var reference = ToRef(loc, entry.Name, entry.Length, entry.LastModified);
                if (filter is null || filter.Includes(reference))
                {
                    lock (sink)
                    {
                        sink.Add(reference);
                    }
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
    /// Lists every file under <paramref name="prefix"/> in a single flat, paginated enumeration, keeping those whose
    /// file name matches the glob. Used when recursing with no directory-level filter, where a flat scan is equivalent
    /// to the hierarchical walk but avoids a round-trip per virtual folder. Directories (ADLS Gen2 directories and
    /// their marker blobs) are not files and are skipped.
    /// </summary>
    private static async Task FlatListAsync(
        AzureStorageContainer container,
        AzureBlobLocation loc,
        string prefix,
        string pattern,
        List<FileRef> sink,
        CancellationToken ct)
    {
        await foreach (var entry in container.ListAsync(prefix, recursive: true, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            var name = LastSegment(entry.Name);
            if (entry.IsDirectory || name.Length == 0 || !FileSystemName.MatchesSimpleExpression(pattern, name))
            {
                continue;
            }

            sink.Add(ToRef(loc, entry.Name, entry.Length, entry.LastModified));
        }
    }

    private static FileRef ToRef(AzureBlobLocation loc, string blobName, long size, DateTimeOffset? modified) => new()
    {
        Path = loc.UriFor(blobName),
        Name = LastSegment(blobName),
        Size = size,
        Modified = modified,
    };

    private static string LastSegment(string blobName)
    {
        var trimmed = blobName.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }
}
