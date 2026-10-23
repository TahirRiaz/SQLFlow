using System.IO.Compression;
using Azure;
using Azure.Identity;
using SqlFlow.Azure.Storage;
using SqlFlow.Core;
using SqlFlow.Core.Export;

namespace SqlFlow.Azure;

/// <summary>
/// Azure Blob / ADLS Gen2 export destination: writes export files to <c>abfss://</c>, <c>wasbs://</c>, and the
/// <c>https://&lt;account&gt;.blob|dfs.core.windows.net</c> URL forms, so an <c>exp</c> flow lands CSV/Parquet in
/// a data lake exactly as it does on local disk. It is the write-side peer of <see cref="AzureBlobFileStore"/>
/// and shares its plumbing: the same <see cref="AzureStorageAccess"/> (the account reached over its Blob or its Data
/// Lake endpoint, whichever this host can reach, authenticated by <c>SQLFLOW_AZURE_AUTH</c>: managed identity,
/// service principal, or <c>az login</c>, with no per-flow secret) and the same <see cref="AzureBlobLocation"/> URI
/// parser. The identity needs write access to the container (the built-in <c>Storage Blob Data Contributor</c> role).
/// </summary>
public sealed class AzureBlobExportDestination : IExportDestination
{
    private readonly AzureStorageAccess _storage;

    public AzureBlobExportDestination(AzureStorageAccess storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
    }

    public bool CanHandle(string location) => AzureBlobLocation.IsAzureStorageUri(location);

    public async Task<Stream> OpenWriteAsync(string location, CancellationToken ct = default)
    {
        var loc = AzureBlobLocation.Parse(location);
        try
        {
            var container = _storage.Container(loc);
            // The container is the blob analogue of the parent folder the interface asks a destination to create.
            // Folders need no creation (a blob's name implies them; a Data Lake write creates them); the container
            // does. Idempotent: a no-op when it already exists.
            await container.CreateIfNotExistsAsync(ct).ConfigureAwait(false);
            // Creates or truncates, matching LocalExportDestination's FileMode.Create. The returned stream buffers and
            // commits its bytes on flush/dispose, and the caller owns that lifetime.
            return await container.OpenWriteAsync(loc.BlobPath, bufferSize: null, ct).ConfigureAwait(false);
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
            await _storage.Container(loc).DeleteIfExistsAsync(loc.BlobPath, ct).ConfigureAwait(false);
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
            var props = await _storage.Container(loc).GetPropertiesAsync(loc.BlobPath, ct).ConfigureAwait(false);
            return props?.Length ?? 0;
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
            return await _storage.Container(loc).OpenSeekableReadAsync(loc.BlobPath, bufferSize: null, ct).ConfigureAwait(false);
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
        var container = _storage.Container(loc);
        var zipBlobPath = Path.ChangeExtension(loc.BlobPath, ".zip");
        var entryName = LastSegment(loc.BlobPath);

        var tempSource = Path.GetTempFileName();
        var tempZip = Path.GetTempFileName();
        try
        {
            try
            {
                var source = await container.OpenReadAsync(loc.BlobPath, ct).ConfigureAwait(false);
                await using (source.ConfigureAwait(false))
                {
                    var target = new FileStream(tempSource, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
                    await using (target.ConfigureAwait(false))
                    {
                        await source.CopyToAsync(target, ct).ConfigureAwait(false);
                    }
                }
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

            var zipContent = new FileStream(tempZip, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous);
            await using (zipContent.ConfigureAwait(false))
            {
                await container.UploadAsync(zipBlobPath, zipContent, overwrite: true, contentHash: null, ct).ConfigureAwait(false);
            }

            await container.DeleteIfExistsAsync(loc.BlobPath, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Turns an Azure SDK failure into an actionable <see cref="SqlFlowException"/>: an auth failure points at the
    /// credential setup, a request failure names the account, container, the endpoint it went to, and HTTP status,
    /// and anything else carries the account, container, and cause. Mirrors <see cref="AzureBlobFileStore"/> so read
    /// and write surface the same diagnostics.
    /// </summary>
    private static SqlFlowException Translate(AzureBlobLocation loc, Exception ex)
    {
        var cause = Unwrap(ex);
        var over = AzureStorageFailures.EndpointOf(ex) is { } endpoint ? $" over the {endpoint.HostLabel()} endpoint" : string.Empty;
        return cause switch
        {
            AuthenticationFailedException or CredentialUnavailableException => new SqlFlowException(
                $"Azure authentication failed for storage account '{loc.Account}'. Sign in with 'az login', or set "
                + "SQLFLOW_AZURE_AUTH and the AZURE_* variables for a service principal or managed identity "
                + $"({cause.Message}).", ex),
            // A network rejection is a 403 too, but no role grants a network path; it reads as the request failure below.
            RequestFailedException { Status: 403 } rfe when !AzureStorageFailures.IsNetworkRejection(rfe) => new SqlFlowException(
                $"Azure Storage denied the export write to '{loc.Account}/{loc.Container}'{over} (status 403): {rfe.Message}. "
                + "Grant the identity the 'Storage Blob Data Contributor' role on the account or container.", ex),
            RequestFailedException rfe => new SqlFlowException(
                $"Azure Storage request failed for '{loc.Account}/{loc.Container}'{over} (status {rfe.Status}): {rfe.Message}", ex),
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
