using System.Collections.Concurrent;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using SqlFlow.Azure;
using SqlFlow.Core;

namespace SqlFlow.Acquire.Landing;

/// <summary>
/// Lands raw payloads to Azure Blob / ADLS Gen2 through the shared <see cref="IAzureCredentialFactory"/> (managed
/// identity / az login / service principal) - the same credential the read-side store, Key Vault, and invoke use,
/// so a landed file needs no per-flow storage secret. Accepts the <c>abfss</c>/<c>wasbs</c> authority form and the
/// <c>https://&lt;account&gt;.blob|dfs.core.windows.net</c> URL form via <see cref="AzureBlobLocation"/>. One blob
/// service client per account is cached for the store's lifetime so the token cache is reused across a run.
/// </summary>
public sealed class AzureRawLandingStore : IRawLandingStore
{
    private readonly IAzureCredentialFactory _credentials;
    private readonly ConcurrentDictionary<Uri, Lazy<BlobServiceClient>> _services = new();

    public AzureRawLandingStore(IAzureCredentialFactory credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        _credentials = credentials;
    }

    public bool CanHandle(string location) => AzureBlobLocation.IsAzureStorageUri(location);

    public string Combine(string baseLocation, string relativePath)
        => $"{baseLocation.TrimEnd('/')}/{relativePath.TrimStart('/')}";

    public async Task<bool> ExistsAsync(string location, CancellationToken ct = default)
    {
        var blob = BlobClient(location);
        try
        {
            return await blob.ExistsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not SqlFlowException and not OperationCanceledException)
        {
            throw Translate(location, ex);
        }
    }

    public async Task<bool> PutAsync(string location, ReadOnlyMemory<byte> content, bool overwrite, bool skipUnchanged = true, CancellationToken ct = default)
    {
        var blob = BlobClient(location);
        try
        {
            if (!overwrite)
            {
                // Fail (rather than clobber) if the blob already exists.
                var options = new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } };
                using var stream = new ReadOnlyMemoryStream(content);
                await blob.UploadAsync(stream, options, ct).ConfigureAwait(false);
                return true;
            }

            if (!skipUnchanged)
            {
                // Write unconditionally, still stamping the MD5 so a later run can compare if the flow re-enables it.
                await UploadStampedAsync(blob, content, ContentHash.Md5(content.Span), ct).ConfigureAwait(false);
                return true;
            }

            // Overwriting: skip when the blob already holds byte-identical content (its ContentHash is the MD5 we stamp
            // below), so an unchanged re-land does not bump LastModified and re-trigger the downstream file flow.
            return await ConditionalWrite.WriteIfChangedAsync(
                content,
                token => BlobContentHashAsync(blob, token),
                (md5, token) => UploadStampedAsync(blob, content, md5, token),
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
        var service = _services.GetOrAdd(
            loc.BlobServiceEndpoint,
            endpoint => new Lazy<BlobServiceClient>(() => new BlobServiceClient(endpoint, _credentials.Create()))).Value;
        var container = service.GetBlobContainerClient(loc.Container);

        // The base addresses a folder, so the prefix is its path plus a separator: without the trailing '/' a sibling
        // folder sharing the name's prefix (".../omsetning_old") would be enumerated as if it were inside this one.
        var prefix = loc.BlobPath.Trim('/');
        prefix = prefix.Length == 0 ? string.Empty : prefix + "/";

        var names = new List<string>();
        try
        {
            // Metadata is requested explicitly: the ADLS directory marker lives there, and without the trait the
            // dictionary comes back empty so every folder placeholder would be mistaken for a landed file.
            await foreach (var blob in container
                .GetBlobsAsync(BlobTraits.Metadata, prefix: prefix, cancellationToken: ct)
                .ConfigureAwait(false))
            {
                // ADLS Gen2 folders surface as zero-length blobs carrying the directory metadata marker; they are
                // placeholders, not landed payloads, so they never contribute a watermark candidate.
                if (blob.Properties.ContentLength is 0 or null
                    && blob.Metadata is { } metadata
                    && metadata.ContainsKey("hdi_isfolder"))
                {
                    continue;
                }

                names.Add(blob.Name[prefix.Length..]);
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

    private static async Task UploadStampedAsync(BlobClient blob, ReadOnlyMemory<byte> content, byte[] md5, CancellationToken ct)
    {
        var options = new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentHash = md5 } };
        using var stream = new ReadOnlyMemoryStream(content);
        await blob.UploadAsync(stream, options, ct).ConfigureAwait(false);
    }

    private static async Task<byte[]?> BlobContentHashAsync(BlobClient blob, CancellationToken ct)
    {
        try
        {
            var props = await blob.GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false);
            return props.Value.ContentHash;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    private BlobClient BlobClient(string location)
    {
        var loc = AzureBlobLocation.Parse(location);
        var service = _services.GetOrAdd(
            loc.BlobServiceEndpoint,
            endpoint => new Lazy<BlobServiceClient>(() => new BlobServiceClient(endpoint, _credentials.Create()))).Value;
        return service.GetBlobContainerClient(loc.Container).GetBlobClient(loc.BlobPath);
    }

    private static SqlFlowException Translate(string location, Exception ex)
    {
        var cause = ex is AggregateException { InnerException: { } inner } ? inner : ex;
        return cause switch
        {
            AuthenticationFailedException or CredentialUnavailableException => new SqlFlowException(
                $"Azure authentication failed while landing to '{location}'. Sign in with 'az login', or set "
                + $"SQLFLOW_AZURE_AUTH and the AZURE_* variables for a service principal or managed identity ({cause.Message}).", ex),
            RequestFailedException rfe => new SqlFlowException(
                $"Azure Storage request failed while landing to '{location}' (status {rfe.Status}): {rfe.Message}", ex),
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
