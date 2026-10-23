using System.Buffers;
using System.Runtime.CompilerServices;

namespace SqlFlow.Azure.Storage;

/// <summary>
/// A container (a Data Lake file system) over the Data Lake Storage endpoint. It gives the results the Blob endpoint
/// gives for the same container: a listing names files and directories by their full in-container path, a missing
/// directory lists nothing, a missing path has no properties, a write of a whole file stamps the same stored MD5 and
/// refuses an existing path the same way (status 409) when it may not overwrite.
/// <para>A write is the Data Lake protocol (create, append, flush) the Data Lake SDK and Hadoop's ABFS driver use, so
/// the file exists, empty, from its create until its final flush, where a Blob upload appears whole. A whole-file
/// upload that fails after its create removes the file it created, so a later create-only write is not refused by
/// what a failed one left behind.</para>
/// </summary>
internal sealed class DfsStorageBackend : IAzureStorageBackend
{
    /// <summary>The size of one append: the Blob SDK's block size for the same uploads.</summary>
    private const int AppendSize = 4 * 1024 * 1024;

    private readonly DfsRestClient _client;
    private readonly DfsRestClient _probe;
    private readonly string _fileSystem;

    public DfsStorageBackend(DfsRestClient client, DfsRestClient probe, string fileSystem)
    {
        _client = client;
        _probe = probe;
        _fileSystem = fileSystem;
    }

    public AzureStorageEndpoint Endpoint => AzureStorageEndpoint.Dfs;

    public Task ProbeAsync(CancellationToken ct) => _probe.GetFileSystemPropertiesAsync(_fileSystem, ct);

    public async IAsyncEnumerable<AzureStorageItem> ListAsync(string prefix, bool recursive, [EnumeratorCancellation] CancellationToken ct)
    {
        var directory = prefix.TrimEnd('/');
        string? continuation = null;
        do
        {
            var (items, next) = await _client
                .ListPathsAsync(_fileSystem, directory.Length == 0 ? null : directory, recursive, continuation, ct)
                .ConfigureAwait(false);
            foreach (var item in items)
            {
                yield return item;
            }

            continuation = next;
        }
        while (continuation is not null);
    }

    public Task<AzureStorageProperties?> GetPropertiesAsync(string path, CancellationToken ct)
        => _client.GetPathPropertiesAsync(_fileSystem, path.TrimEnd('/'), ct);

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct)
        => DfsReadStream.OpenAsync(_client, _fileSystem, path, 0, null, ct);

    public Task<Stream> OpenReadAsync(string path, long offset, long length, CancellationToken ct)
    {
        // The same bounds the Blob SDK's HttpRange enforces, so both endpoints refuse the same requests.
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        return DfsReadStream.OpenAsync(_client, _fileSystem, path, offset, length, ct);
    }

    public Task<Stream> OpenSeekableReadAsync(string path, int? bufferSize, CancellationToken ct)
        => DfsSeekableReadStream.OpenAsync(_client, _fileSystem, path, bufferSize, ct);

    public async Task UploadAsync(string path, Stream content, bool overwrite, byte[]? contentHash, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        await _client.CreateFileAsync(_fileSystem, path, overwrite, ct).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(AppendSize);
        try
        {
            long position = 0;
            while (true)
            {
                var filled = 0;
                while (filled < AppendSize)
                {
                    var read = await content.ReadAsync(buffer.AsMemory(filled, AppendSize - filled), ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    filled += read;
                }

                if (filled == 0)
                {
                    break;
                }

                await _client.AppendAsync(_fileSystem, path, position, buffer.AsMemory(0, filled), async: true, ct).ConfigureAwait(false);
                position += filled;
                if (filled < AppendSize)
                {
                    break;
                }
            }

            await _client.FlushAsync(_fileSystem, path, position, contentHash, close: true, async: true, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The create succeeded (it is outside this block, so a refused create-only write never removes the file that
            // refused it); what it made is unfinished, so it goes before the upload's own failure propagates.
            await RemoveUnfinishedAsync(path).ConfigureAwait(false);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task<Stream> OpenWriteAsync(string path, int? bufferSize, CancellationToken ct)
    {
        await _client.CreateFileAsync(_fileSystem, path, overwrite: true, ct).ConfigureAwait(false);
        return new DfsWriteStream(_client, _fileSystem, path, bufferSize);
    }

    public async Task<bool> DeleteIfExistsAsync(string path, CancellationToken ct)
        => await _client.DeletePathAsync(_fileSystem, path, async: true, ct).ConfigureAwait(false);

    public async Task CreateContainerIfNotExistsAsync(CancellationToken ct)
        => await _client.CreateFileSystemAsync(_fileSystem, ct).ConfigureAwait(false);

    /// <summary>
    /// Removes the file an upload created and could not finish, so the upload's own failure is what the caller sees and
    /// nothing half-written stays behind. The removal is never cancelled by the caller's token (a cancelled upload still
    /// cleans up). If it fails too, the upload's failure still stands as the one that explains what happened, and the
    /// empty file is replaced by the next overwrite or removed by the next delete.
    /// </summary>
    private async Task RemoveUnfinishedAsync(string path)
    {
        try
        {
            await _client.DeletePathAsync(_fileSystem, path, async: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception cleanup) when (cleanup is not OutOfMemoryException)
        {
            // Not rethrown: replacing the upload's failure with the cleanup's would hide the cause.
        }
    }
}
