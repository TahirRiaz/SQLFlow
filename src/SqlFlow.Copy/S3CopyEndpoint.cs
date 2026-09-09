using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using SqlFlow.Core;
using SqlFlow.Core.Copy;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Copy;

/// <summary>
/// The AWS S3 copy endpoint (<c>s3://bucket/prefix</c>): it lists, reads, and writes S3 objects, so a copy flow can
/// pull a vendor's S3 export into the lake (S3 as source) or push files to S3 (S3 as target) with the one copy engine.
/// Credentials are an access-key / secret-key pair of secret references on the endpoint (<c>accessKeyRef</c> /
/// <c>secretKeyRef</c>) with a <c>region</c> (default <c>eu-west-1</c>); S3 has no ambient managed identity like Azure,
/// so the keys are required. One <see cref="AmazonS3Client"/> is built per resolved (region, keys) and reused across
/// the run. Object content hashes come from the S3 ETag when it is a plain MD5 (a single-part upload), which lets the
/// engine skip an unchanged object without downloading it; a multipart ETag is not an MD5, so it reports no hash and
/// the engine falls back to reading and hashing the bytes.
/// </summary>
public sealed class S3CopyEndpoint : ICopyEndpoint
{
    private readonly ISecretResolver _secrets;

    public S3CopyEndpoint(ISecretResolver secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        _secrets = secrets;
    }

    public bool CanHandle(string location)
        => location.StartsWith("s3://", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<CopyItem> ListAsync(
        CopyEndpoint endpoint, CopyModifiedWindow window, [EnumeratorCancellation] CancellationToken ct)
    {
        var loc = S3Location.Parse(endpoint.Location);
        var prefix = loc.Prefix;
        using var client = await ClientAsync(endpoint, ct).ConfigureAwait(false);

        string? continuationToken = null;
        do
        {
            var response = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = loc.Bucket,
                Prefix = prefix.Length == 0 ? null : prefix,
                ContinuationToken = continuationToken,
            }, ct).ConfigureAwait(false);

            foreach (var obj in response.S3Objects)
            {
                ct.ThrowIfCancellationRequested();
                var key = obj.Key;
                var leaf = key[(key.LastIndexOf('/') + 1)..];
                if (leaf.Length == 0)
                {
                    continue; // a folder-marker key
                }

                var relative = prefix.Length == 0 ? key : key[prefix.Length..].TrimStart('/');

                // 'recursive: false' keeps only objects directly under the prefix (no further '/').
                if (!endpoint.Recursive && relative.Contains('/', StringComparison.Ordinal))
                {
                    continue;
                }

                if (!FileSystemName.MatchesSimpleExpression(endpoint.Pattern, leaf))
                {
                    continue;
                }

                var modified = new DateTimeOffset(obj.LastModified.ToUniversalTime(), TimeSpan.Zero);
                if (!window.Includes(modified))
                {
                    continue;
                }

                yield return new CopyItem(key, relative, leaf, modified, obj.Size, Md5FromETag(obj.ETag));
            }

            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (continuationToken is not null);
    }

    public async Task<byte[]> ReadAsync(CopyEndpoint endpoint, string absolutePath, CancellationToken ct)
    {
        var loc = S3Location.Parse(endpoint.Location);
        using var client = await ClientAsync(endpoint, ct).ConfigureAwait(false);
        try
        {
            using var response = await client.GetObjectAsync(loc.Bucket, absolutePath, ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, ct).ConfigureAwait(false);
            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is not SqlFlowException and not OperationCanceledException)
        {
            throw Translate(endpoint.Location, ex);
        }
    }

    public async Task<IReadOnlyDictionary<string, byte[]?>> TargetHashIndexAsync(CopyEndpoint endpoint, CancellationToken ct)
    {
        var loc = S3Location.Parse(endpoint.Location);
        var prefix = loc.Prefix;
        var index = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        using var client = await ClientAsync(endpoint, ct).ConfigureAwait(false);
        try
        {
            string? continuationToken = null;
            do
            {
                var response = await client.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = loc.Bucket,
                    Prefix = prefix.Length == 0 ? null : prefix,
                    ContinuationToken = continuationToken,
                }, ct).ConfigureAwait(false);

                foreach (var obj in response.S3Objects)
                {
                    var key = obj.Key;
                    var leaf = key[(key.LastIndexOf('/') + 1)..];
                    if (leaf.Length == 0)
                    {
                        continue;
                    }

                    var relative = prefix.Length == 0 ? key : key[prefix.Length..].TrimStart('/');
                    index[relative] = Md5FromETag(obj.ETag);
                }

                continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
            }
            while (continuationToken is not null);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // The target bucket/prefix does not exist yet: nothing landed, so every file is new.
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
        var loc = S3Location.Parse(endpoint.Location);
        using var client = await ClientAsync(endpoint, ct).ConfigureAwait(false);
        try
        {
            var metadata = await client.GetObjectMetadataAsync(loc.Bucket, loc.KeyFor(relativePath), ct).ConfigureAwait(false);
            return Md5FromETag(metadata.ETag);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception ex) when (ex is not SqlFlowException and not OperationCanceledException)
        {
            throw Translate(endpoint.Location, ex);
        }
    }

    public async Task<string> WriteAsync(
        CopyEndpoint endpoint, string relativePath, ReadOnlyMemory<byte> content, bool overwrite, byte[] contentHash, CancellationToken ct)
    {
        var loc = S3Location.Parse(endpoint.Location);
        var key = loc.KeyFor(relativePath);
        using var client = await ClientAsync(endpoint, ct).ConfigureAwait(false);
        try
        {
            if (!overwrite && await ObjectExistsAsync(client, loc.Bucket, key, ct).ConfigureAwait(false))
            {
                throw new SqlFlowException($"Copy target object 's3://{loc.Bucket}/{key}' already exists and overwrite is disabled.");
            }

            // A single PutObject stores the object's MD5 as its ETag, which TargetHashIndex/TargetContentHash read back
            // to skip an unchanged object next run - so no separate hash stamping is needed.
            using var stream = new MemoryStream(content.ToArray(), writable: false);
            await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = loc.Bucket,
                Key = key,
                InputStream = stream,
                AutoCloseStream = false,
            }, ct).ConfigureAwait(false);
            return $"s3://{loc.Bucket}/{key}";
        }
        catch (Exception ex) when (ex is not SqlFlowException and not OperationCanceledException)
        {
            throw Translate(endpoint.Location, ex);
        }
    }

    private static async Task<bool> ObjectExistsAsync(IAmazonS3 client, string bucket, string key, CancellationToken ct)
    {
        try
        {
            await client.GetObjectMetadataAsync(bucket, key, ct).ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private async Task<AmazonS3Client> ClientAsync(CopyEndpoint endpoint, CancellationToken ct)
    {
        var accessKey = await ResolveRequired(endpoint.AccessKeyRef, "accessKeyRef", endpoint.Location, ct).ConfigureAwait(false);
        var secretKey = await ResolveRequired(endpoint.SecretKeyRef, "secretKeyRef", endpoint.Location, ct).ConfigureAwait(false);
        var region = string.IsNullOrWhiteSpace(endpoint.Region) ? "eu-west-1" : endpoint.Region!.Trim();
        return new AmazonS3Client(accessKey, secretKey, RegionEndpoint.GetBySystemName(region));
    }

    private async Task<string> ResolveRequired(string? secretRef, string field, string location, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
        {
            throw new SqlFlowException($"The S3 copy endpoint '{location}' requires '{field}' (an access-key/secret-key pair; S3 has no ambient identity).");
        }

        return await _secrets.ResolveAsync(secretRef, ct).ConfigureAwait(false);
    }

    /// <summary>The object's MD5 bytes when the ETag is a plain 32-hex MD5 (a single-part upload); null for a multipart
    /// ETag (the <c>&lt;hex&gt;-&lt;n&gt;</c> form), which is not an MD5, so the engine reads and hashes instead.</summary>
    internal static byte[]? Md5FromETag(string? etag)
    {
        if (string.IsNullOrEmpty(etag))
        {
            return null;
        }

        var hex = etag.Trim('"');
        if (hex.Length != 32)
        {
            return null;
        }

        foreach (var c in hex)
        {
            if (!Uri.IsHexDigit(c))
            {
                return null;
            }
        }

        return Convert.FromHexString(hex);
    }

    private static SqlFlowException Translate(string location, Exception ex)
    {
        var cause = ex is AggregateException { InnerException: { } inner } ? inner : ex;
        return cause switch
        {
            AmazonS3Exception s3 => new SqlFlowException(
                $"AWS S3 request failed for copy endpoint '{location}' (status {s3.StatusCode}): {s3.Message}", ex),
            _ => new SqlFlowException($"Could not access S3 copy endpoint '{location}': {cause.Message}", ex),
        };
    }

    /// <summary>A parsed <c>s3://bucket/prefix</c> location.</summary>
    internal readonly record struct S3Location(string Bucket, string Prefix)
    {
        public static S3Location Parse(string location)
        {
            if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) || !uri.Scheme.Equals("s3", StringComparison.OrdinalIgnoreCase))
            {
                throw new SqlFlowException($"An S3 copy location must be 's3://bucket[/prefix]', got '{location}'.");
            }

            var bucket = uri.Host;
            if (string.IsNullOrEmpty(bucket))
            {
                throw new SqlFlowException($"The S3 copy location '{location}' has no bucket.");
            }

            // The prefix is a LITERAL S3 key prefix, not a folder: it is used verbatim as the ListObjectsV2 prefix and
            // matches by string prefix, so 'export_orders' selects the flat keys 'export_orders_2021_...'. A trailing
            // slash in the authored URL is not meaningful to S3, so it is trimmed; a relative path composed under the
            // prefix (KeyFor, for an S3 target) inserts the separator explicitly.
            var prefix = uri.AbsolutePath.Trim('/');
            return new S3Location(bucket, prefix);
        }

        /// <summary>The absolute object key for a path relative to the prefix (S3 as a target).</summary>
        public string KeyFor(string relativePath)
        {
            var rel = relativePath.Replace('\\', '/').TrimStart('/');
            return Prefix.Length == 0 ? rel : $"{Prefix}/{rel}";
        }
    }
}
