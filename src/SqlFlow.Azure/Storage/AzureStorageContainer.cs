using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;

namespace SqlFlow.Azure.Storage;

/// <summary>
/// One Azure Storage container (an ADLS Gen2 file system), reached over whichever of its account's endpoints this host
/// can reach. Every operation gives the same result over either endpoint. The first request of the process to the account
/// decides the endpoint (<see cref="AzureStorageEndpointSelector"/>); a later request the network refuses there before
/// the service processed it (a network rejection, or no connection at all) makes the account's endpoint be decided again,
/// and the request is sent once more over the other endpoint when that is now the one. A request that may have been
/// processed is never sent twice. A failed request's exception records the endpoint it went to
/// (<see cref="AzureStorageFailures.EndpointOf"/>).
/// <para>Paths are in-container paths without a leading slash. A listing prefix names a directory: empty for the
/// container root, otherwise with or without its trailing slash.</para>
/// </summary>
public sealed class AzureStorageContainer
{
    private readonly AzureStorageAccess _access;
    private readonly AzureBlobLocation _location;
    private readonly AzureStorageAuth _auth;
    private readonly Lock _gate = new();
    private ContainerBackends? _backends;

    internal AzureStorageContainer(AzureStorageAccess access, AzureBlobLocation location, AzureStorageAuth auth)
    {
        _access = access;
        _location = location;
        _auth = auth;
    }

    /// <summary>The storage account's name.</summary>
    public string Account => _location.Account;

    /// <summary>The container's name.</summary>
    public string Name => _location.Container;

    /// <summary>The endpoint this container's requests go to, deciding it if no request has yet.</summary>
    public async Task<AzureStorageEndpoint> EndpointAsync(CancellationToken ct = default)
    {
        var backends = Backends();
        return await _access.Selector.SelectAsync(backends.Target, ct).ConfigureAwait(false);
    }

    /// <summary>The files and directories under the <paramref name="prefix"/> directory: every level when
    /// <paramref name="recursive"/>, otherwise those directly under it. A directory that does not exist lists nothing; a
    /// container that does not exist fails with status 404.</summary>
    public IAsyncEnumerable<AzureStorageItem> ListAsync(string prefix, bool recursive, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        var directory = prefix.Trim('/');
        return ListCoreAsync(directory.Length == 0 ? string.Empty : directory + "/", recursive, ct);
    }

    /// <summary>The path's properties, or <c>null</c> when nothing exists there.</summary>
    public Task<AzureStorageProperties?> GetPropertiesAsync(string path, CancellationToken ct = default)
        => RunAsync(backend => backend.GetPropertiesAsync(RequirePath(path), ct), ct);

    /// <summary>A forward-only stream over the whole file.</summary>
    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
        => RunAsync(backend => backend.OpenReadAsync(RequirePath(path), ct), ct);

    /// <summary>A forward-only stream over <paramref name="length"/> bytes of the file from <paramref name="offset"/>.</summary>
    public Task<Stream> OpenReadAsync(string path, long offset, long length, CancellationToken ct = default)
        => RunAsync(backend => backend.OpenReadAsync(RequirePath(path), offset, length, ct), ct);

    /// <summary>A seekable stream over the file that fetches <paramref name="bufferSize"/> bytes at a time (4 MB by
    /// default) and fails (status 412) if the file changes while it is read.</summary>
    public Task<Stream> OpenSeekableReadAsync(string path, int? bufferSize = null, CancellationToken ct = default)
        => RunAsync(backend => backend.OpenSeekableReadAsync(RequirePath(path), bufferSize, ct), ct);

    /// <summary>The whole file's bytes.</summary>
    public async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct = default)
    {
        var stream = await OpenReadAsync(path, ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
            return buffer.ToArray();
        }
    }

    /// <summary>
    /// Writes the whole file from <paramref name="content"/>, stamping <paramref name="contentHash"/> (the content's MD5)
    /// as the file's stored content hash when given. Without <paramref name="overwrite"/> it fails with status 409 when
    /// the path exists. A seekable <paramref name="content"/> can be sent again over the other endpoint after a network
    /// refusal; a forward-only one cannot, and the refusal surfaces.
    /// </summary>
    public Task UploadAsync(string path, Stream content, bool overwrite, byte[]? contentHash = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var start = content.CanSeek ? content.Position : -1;
        return RunAsync(
            async backend =>
            {
                if (start >= 0)
                {
                    content.Position = start;
                }

                await backend.UploadAsync(RequirePath(path), content, overwrite, contentHash, ct).ConfigureAwait(false);
                return true;
            },
            ct,
            canResend: content.CanSeek);
    }

    /// <summary>Creates or truncates the file and returns a stream whose bytes are committed on flush and dispose.</summary>
    public Task<Stream> OpenWriteAsync(string path, int? bufferSize = null, CancellationToken ct = default)
        => RunAsync(backend => backend.OpenWriteAsync(RequirePath(path), bufferSize, ct), ct);

    /// <summary>Deletes the file; false when nothing was there.</summary>
    public Task<bool> DeleteIfExistsAsync(string path, CancellationToken ct = default)
        => RunAsync(backend => backend.DeleteIfExistsAsync(RequirePath(path), ct), ct);

    /// <summary>Creates the container when it does not exist.</summary>
    public Task CreateIfNotExistsAsync(CancellationToken ct = default)
        => RunAsync(
            async backend =>
            {
                await backend.CreateContainerIfNotExistsAsync(ct).ConfigureAwait(false);
                return true;
            },
            ct);

    private ContainerBackends Backends()
    {
        lock (_gate)
        {
            return _backends ??= _access.Connect(_location, _auth);
        }
    }

    private async Task<T> RunAsync<T>(Func<IAzureStorageBackend, Task<T>> operation, CancellationToken ct, bool canResend = true)
    {
        var backends = Backends();
        var backend = backends.Backend(await _access.Selector.SelectAsync(backends.Target, ct).ConfigureAwait(false));
        try
        {
            return await operation(backend).ConfigureAwait(false);
        }
        catch (Exception ex) when (canResend && MaySwitch(backends, ex))
        {
            var next = await SwitchAsync(backends, backend.Endpoint, ex, ct).ConfigureAwait(false);
            try
            {
                return await operation(next).ConfigureAwait(false);
            }
            catch (Exception retried)
            {
                Record(retried, next.Endpoint);
                throw;
            }
        }
        catch (Exception ex)
        {
            Record(ex, backend.Endpoint);
            throw;
        }
    }

    private async IAsyncEnumerable<AzureStorageItem> ListCoreAsync(string prefix, bool recursive, [EnumeratorCancellation] CancellationToken ct)
    {
        var backends = Backends();
        var backend = backends.Backend(await _access.Selector.SelectAsync(backends.Target, ct).ConfigureAwait(false));
        var items = backend.ListAsync(prefix, recursive, ct).GetAsyncEnumerator(ct);
        try
        {
            bool more;
            try
            {
                more = await items.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (MaySwitch(backends, ex))
            {
                // Nothing has been returned yet, so the listing can start over on the other endpoint.
                await items.DisposeAsync().ConfigureAwait(false);
                backend = await SwitchAsync(backends, backend.Endpoint, ex, ct).ConfigureAwait(false);
                items = backend.ListAsync(prefix, recursive, ct).GetAsyncEnumerator(ct);
                more = await NextAsync(items, backend.Endpoint).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Record(ex, backend.Endpoint);
                throw;
            }

            while (more)
            {
                yield return items.Current;
                more = await NextAsync(items, backend.Endpoint).ConfigureAwait(false);
            }
        }
        finally
        {
            await items.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async ValueTask<bool> NextAsync(IAsyncEnumerator<AzureStorageItem> items, AzureStorageEndpoint endpoint)
    {
        try
        {
            return await items.MoveNextAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Record(ex, endpoint);
            throw;
        }
    }

    private bool MaySwitch(ContainerBackends backends, Exception ex)
        => _access.Selector.Detects(backends.Target) && AzureStorageFailures.NeverProcessed(ex);

    /// <summary>
    /// Forgets the endpoint that refused a request and decides again. When the decision lands on the other endpoint, the
    /// request goes there; when it lands on the same one (it answers a probe but refused the request), the refusal stands.
    /// When neither endpoint answers, that diagnosis (which names both) is what surfaces.
    /// </summary>
    private async Task<IAzureStorageBackend> SwitchAsync(ContainerBackends backends, AzureStorageEndpoint failed, Exception refusal, CancellationToken ct)
    {
        _access.Selector.Forget(backends.Target, failed);
        var next = await _access.Selector.SelectAsync(backends.Target, ct).ConfigureAwait(false);
        if (next == failed)
        {
            Record(refusal, failed);
            ExceptionDispatchInfo.Throw(refusal);
        }

        _access.Logger.LogInformation(
            "Azure Storage account {Account} moved from its {From} endpoint to its {To} endpoint: the {From} endpoint refused a request ({Reason}).",
            backends.Target.Account, failed.HostLabel(), next.HostLabel(), failed.HostLabel(), AzureStorageFailures.Describe(refusal));
        return backends.Backend(next);
    }

    private static void Record(Exception exception, AzureStorageEndpoint endpoint)
    {
        if (exception is not SqlFlowException && !exception.Data.Contains(AzureStorageFailures.EndpointDataKey))
        {
            exception.Data[AzureStorageFailures.EndpointDataKey] = endpoint;
        }
    }

    private static string RequirePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return path.TrimStart('/');
    }
}
