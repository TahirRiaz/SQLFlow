using System.Collections.Concurrent;
using System.IO.Compression;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using SqlFlow.Core;
using SqlFlow.Core.Export;

namespace SqlFlow.Azure;

/// <summary>
/// Azure Blob / ADLS Gen2 export destination: writes export files to <c>abfss://</c>, <c>wasbs://</c>, and the
/// <c>https://&lt;account&gt;.blob|dfs.core.windows.net</c> URL forms, so an <c>exp</c> flow lands CSV/Parquet in
/// a data lake exactly as it does on local disk. It is the write-side peer of <see cref="AzureBlobFileStore"/>
/// and shares its plumbing: the same <see cref="IAzureCredentialFactory"/> (honoring <c>SQLFLOW_AZURE_AUTH</c>:
/// managed identity, service principal, or <c>az login</c>, with no per-flow secret), the same
/// <see cref="AzureBlobLocation"/> URI parser, and one cached <see cref="BlobServiceClient"/> per endpoint.
/// The identity needs write access to the container (the built-in <c>Storage Blob Data Contributor</c> role).
/// </summary>
public sealed class AzureBlobExportDestination : IExportDestination
{
    private readonly IAzureCredentialFactory _credentials;

    // One service client per storage endpoint, created lazily and reused so the credential's token cache is
    // shared across every write in a run (and across runs) instead of re-authenticating per file.
    private readonly ConcurrentDictionary<Uri, Lazy<BlobServiceClient>> _services = new();

    public AzureBlobExportDestination(IAzureCredentialFactory credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        _credentials = credentials;
    }

    public bool CanHandle(string location) => AzureBlobLocation.IsAzureStorageUri(location);

    public async Task<Stream> OpenWriteAsync(string location, CancellationToken ct = default)
    {
        var loc = AzureBlobLocation.Parse(location);
        try
        {
            var container = ContainerClient(loc);
            // The container is the blob analogue of the parent folder the interface asks a destination to create.
            // Blob virtual directories need no creation (they are implied by the blob name); the container does.
            // Idempotent: a no-op when it already exists.
            await container.CreateIfNotExistsAsync(cancellationToken: ct).ConfigureAwait(false);
            var blob = container.GetBlobClient(loc.BlobPath);
            // overwrite:true creates or truncates, matching LocalExportDestination's FileMode.Create. The returned
            // stream buffers and commits its blocks on flush/dispose, and the caller owns that lifetime.
            return await blob.OpenWriteAsync(overwrite: true, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not SqlFlowException and not OperationCanceledException)
        {
            throw Translate(loc, ex);
        }
    }

    public async Task DeleteIfExistsAsync(string location, CancellationToken ct = default)
    {
        var loc = AzureBlobLocation.Parse(location);
        try
        {
            await ContainerClient(loc).GetBlobClient(loc.BlobPath).DeleteIfExistsAsync(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not SqlFlowException and not OperationCanceledException)
        {
            throw Translate(loc, ex);
        }
    }

    public async Task<long> GetSizeAsync(string location, CancellationToken ct = default)
    {
        var loc = AzureBlobLocation.Parse(location);
        try
        {
            var props = await ContainerClient(loc).GetBlobClient(loc.BlobPath).GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false);
            return props.Value.ContentLength;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return 0;
        }
        catch (Exception ex) when (ex is not SqlFlowException and not OperationCanceledException)
        {
            throw Translate(loc, ex);
        }
    }

    public async Task<Stream> OpenReadAsync(string location, CancellationToken ct = default)
    {
        var loc = AzureBlobLocation.Parse(location);
        try
        {
            return await ContainerClient(loc).GetBlobClient(loc.BlobPath).OpenReadAsync(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not SqlFlowException and not OperationCanceledException)
        {
            throw Translate(loc, ex);
        }
    }

    /// <summary>
    /// Replaces the blob with a single-entry <c>.zip</c> (legacy zipTrg): download to a temp file, zip it, upload
    /// the archive to the same path with a <c>.zip</c> extension, then delete the original blob. Both temp files
    /// are removed on every exit path. Returns the archive's URI in the caller's original scheme.
    /// </summary>
    public async Task<string> ZipAsync(string location, CancellationToken ct = default)
    {
        var loc = AzureBlobLocation.Parse(location);
        var container = ContainerClient(loc);
        var source = container.GetBlobClient(loc.BlobPath);
        var zipBlobPath = Path.ChangeExtension(loc.BlobPath, ".zip");
        var entryName = LastSegment(loc.BlobPath);

        var tempSource = Path.GetTempFileName();
        var tempZip = Path.GetTempFileName();
        try
        {
            try
            {
                await source.DownloadToAsync(tempSource, ct).ConfigureAwait(false);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                throw new SqlFlowException($"Cannot zip '{location}': the blob does not exist.", ex);
            }

            // Build the archive in a temp file (streamed, not buffered in memory) so an arbitrarily large export
            // zips without pressure. Recreate deterministically: GetTempFileName made an empty placeholder.
            File.Delete(tempZip);
            using (var archive = ZipFile.Open(tempZip, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(tempSource, entryName, CompressionLevel.Optimal);
            }

            var zipBlob = container.GetBlobClient(zipBlobPath);
            await zipBlob.UploadAsync(tempZip, overwrite: true, cancellationToken: ct).ConfigureAwait(false);
            await source.DeleteIfExistsAsync(cancellationToken: ct).ConfigureAwait(false);
            return loc.UriFor(zipBlobPath);
        }
        catch (Exception ex) when (ex is not SqlFlowException and not OperationCanceledException)
        {
            throw Translate(loc, ex);
        }
        finally
        {
            TryDelete(tempSource);
            TryDelete(tempZip);
        }
    }

    private BlobContainerClient ContainerClient(AzureBlobLocation loc)
    {
        var service = _services.GetOrAdd(
            loc.BlobServiceEndpoint,
            endpoint => new Lazy<BlobServiceClient>(() => new BlobServiceClient(endpoint, _credentials.Create()))).Value;
        return service.GetBlobContainerClient(loc.Container);
    }

    /// <summary>
    /// Turns an Azure SDK failure into an actionable <see cref="SqlFlowException"/>: an auth failure points at the
    /// credential setup, a request failure names the account, container, and HTTP status, and anything else
    /// carries the account, container, and cause. Mirrors <see cref="AzureBlobFileStore"/> so read and write
    /// surface the same diagnostics.
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
            RequestFailedException { Status: 403 } rfe => new SqlFlowException(
                $"Azure Storage denied the export write to '{loc.Account}/{loc.Container}' (status 403): {rfe.Message}. "
                + "Grant the identity the 'Storage Blob Data Contributor' role on the account or container.", ex),
            RequestFailedException rfe => new SqlFlowException(
                $"Azure Storage request failed for '{loc.Account}/{loc.Container}' (status {rfe.Status}): {rfe.Message}", ex),
            _ => new SqlFlowException(
                $"Could not write to Azure Storage location '{loc.Account}/{loc.Container}': {cause.Message}", ex),
        };
    }

    private static Exception Unwrap(Exception ex)
        => ex is AggregateException { InnerException: { } inner } ? Unwrap(inner) : ex;

    private static string LastSegment(string blobPath)
    {
        var trimmed = blobPath.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A leftover temp file is harmless; never let cleanup mask the real result.
        }
    }
}
