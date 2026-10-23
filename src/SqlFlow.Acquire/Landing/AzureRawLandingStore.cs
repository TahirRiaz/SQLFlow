using Azure;
using Azure.Identity;
using SqlFlow.Azure;
using SqlFlow.Azure.Storage;
using SqlFlow.Core;

namespace SqlFlow.Acquire.Landing;

/// <summary>
/// Lands raw payloads to Azure Blob / ADLS Gen2 through the shared <see cref="AzureStorageAccess"/>: the account is
/// reached over its Blob or its Data Lake endpoint, whichever this host can reach, with the shared credential (managed
/// identity / az login / service principal) the read-side store, Key Vault, and invoke use, so a landed file needs no
/// per-flow storage secret. Accepts the <c>abfss</c>/<c>wasbs</c> authority form and the
/// <c>https://&lt;account&gt;.blob|dfs.core.windows.net</c> URL form via <see cref="AzureBlobLocation"/>.
/// </summary>
public sealed class AzureRawLandingStore : IRawLandingStore
{
    private readonly AzureStorageAccess _storage;

    public AzureRawLandingStore(AzureStorageAccess storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
    }

    public bool CanHandle(string location) => AzureBlobLocation.IsAzureStorageUri(location);

    public string Combine(string baseLocation, string relativePath)
        => $"{baseLocation.TrimEnd('/')}/{relativePath.TrimStart('/')}";

    public async Task<bool> ExistsAsync(string location, CancellationToken ct = default)
    {
        var loc = AzureBlobLocation.Parse(location);
        try
        {
            return await _storage.Container(loc).GetPropertiesAsync(loc.BlobPath, ct).ConfigureAwait(false) is not null;
        }
        catch (Exception ex) when (ex is not SqlFlowException and not OperationCanceledException)
        {
            throw Translate(location, ex);
        }
    }

    public async Task<bool> PutAsync(string location, ReadOnlyMemory<byte> content, bool overwrite, bool skipUnchanged = true, CancellationToken ct = default)
    {
        var loc = AzureBlobLocation.Parse(location);
        var container = _storage.Container(loc);
        try
        {
            if (!overwrite)
            {
                // Fail (rather than clobber) if the blob already exists.
                using var stream = new ReadOnlyMemoryStream(content);
                await container.UploadAsync(loc.BlobPath, stream, overwrite: false, contentHash: null, ct).ConfigureAwait(false);
                return true;
            }

            if (!skipUnchanged)
            {
                // Write unconditionally, still stamping the MD5 so a later run can compare if the flow re-enables it.
                await UploadStampedAsync(container, loc.BlobPath, content, ContentHash.Md5(content.Span), ct).ConfigureAwait(false);
                return true;
            }

            // Overwriting: skip when the blob already holds byte-identical content (its stored content hash is the MD5
            // we stamp below), so an unchanged re-land does not bump LastModified and re-trigger the downstream file flow.
            return await ConditionalWrite.WriteIfChangedAsync(
                content,
                async token => (await container.GetPropertiesAsync(loc.BlobPath, token).ConfigureAwait(false))?.ContentHash,
                (md5, token) => UploadStampedAsync(container, loc.BlobPath, content, md5, token),
                ct).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (!overwrite && ex.Status == 409)
        {
            throw new SqlFlowException($"Landing blob '{location}' already exists and overwrite is disabled.", ex);
        }
        catch (Exception ex) when (ex is not SqlFlowException and not OperationCanceledException)
        {
            throw Translate(location, ex);
        }
    }

    public async Task<IReadOnlyList<string>> ListNamesAsync(string baseLocation, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseLocation);
        var loc = AzureBlobLocation.Parse(baseLocation);
        var container = _storage.Container(loc);

        // The base addresses a folder, so the prefix is its path plus a separator: without the trailing '/' a sibling
        // folder sharing the name's prefix (".../omsetning_old") would be enumerated as if it were inside this one.
        var prefix = loc.BlobPath.Trim('/');
        prefix = prefix.Length == 0 ? string.Empty : prefix + "/";

        var names = new List<string>();
        try
        {
            await foreach (var item in container.ListAsync(prefix, recursive: true, ct).ConfigureAwait(false))
            {
                // ADLS Gen2 folders (directories, and the zero-length marker blobs the Blob endpoint shows for them) are
                // placeholders, not landed payloads, so they never contribute a watermark candidate.
                if (item.IsDirectory)
                {
                    continue;
                }

                names.Add(item.Name[prefix.Length..]);
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // No container or no such prefix: nothing has landed here yet, which is a legitimate first-run state.
            return [];
        }
        catch (Exception ex) when (ex is not SqlFlowException and not OperationCanceledException)
        {
            throw Translate(baseLocation, ex);
        }

        return names;
    }

    private static async Task UploadStampedAsync(
        AzureStorageContainer container, string path, ReadOnlyMemory<byte> content, byte[] md5, CancellationToken ct)
    {
        using var stream = new ReadOnlyMemoryStream(content);
        await container.UploadAsync(path, stream, overwrite: true, contentHash: md5, ct).ConfigureAwait(false);
    }

    private static SqlFlowException Translate(string location, Exception ex)
    {
        var cause = ex is AggregateException { InnerException: { } inner } ? inner : ex;
        var over = AzureStorageFailures.EndpointOf(ex) is { } endpoint ? $" over the {endpoint.HostLabel()} endpoint" : string.Empty;
        return cause switch
        {
            AuthenticationFailedException or CredentialUnavailableException => new SqlFlowException(
                $"Azure authentication failed while landing to '{location}'. Sign in with 'az login', or set "
                + $"SQLFLOW_AZURE_AUTH and the AZURE_* variables for a service principal or managed identity ({cause.Message}).", ex),
            RequestFailedException rfe => new SqlFlowException(
                $"Azure Storage request failed while landing to '{location}'{over} (status {rfe.Status}): {rfe.Message}", ex),
            _ => new SqlFlowException($"Could not land to Azure Storage location '{location}': {cause.Message}", ex),
        };
    }

    /// <summary>A forward-only stream over a <see cref="ReadOnlyMemory{Byte}"/> so an in-memory payload uploads without a copy.</summary>
    private sealed class ReadOnlyMemoryStream : Stream
    {
        private readonly ReadOnlyMemory<byte> _memory;
        private int _position;

        public ReadOnlyMemoryStream(ReadOnlyMemory<byte> memory) => _memory = memory;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _memory.Length;

        public override long Position
        {
            get => _position;
            set => _position = (int)value;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var remaining = _memory.Length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            var take = Math.Min(remaining, buffer.Length);
            _memory.Span.Slice(_position, take).CopyTo(buffer);
            _position += take;
            return take;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => (int)offset,
                SeekOrigin.Current => _position + (int)offset,
                SeekOrigin.End => _memory.Length + (int)offset,
                _ => _position,
            };
            return _position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
