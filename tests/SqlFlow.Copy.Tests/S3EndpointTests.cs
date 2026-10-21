using Amazon.S3.Model;
using SqlFlow.Copy;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using Xunit;

namespace SqlFlow.Copy.Tests;

/// <summary>
/// Endpoint selection and the S3 endpoint's pure parsing/hashing. The selection tests lock in the fix for the bug
/// where an <c>s3://</c> location was swallowed by the local endpoint (tried first) and read as a CWD-relative path:
/// each scheme must resolve to exactly one endpoint, in the engine's registration order. The object-store targets
/// refuse a relative path with a <c>..</c> segment, as the local endpoint refuses one that leaves its root.
/// </summary>
public sealed class S3EndpointTests
{
    private sealed class NoSecrets : ISecretResolver
    {
        public string Resolve(string value) => value;
        public Task<string> ResolveAsync(string value, CancellationToken ct = default) => Task.FromResult(value);
    }

    // A credential stub: endpoint selection (CanHandle) never authenticates, so Create is never called.
    private sealed class StubCredentials : SqlFlow.Azure.IAzureCredentialFactory
    {
        public global::Azure.Core.TokenCredential Create() => throw new NotSupportedException();
    }

    // The endpoints in the exact order CopyServices registers them, so First-match selection is what the engine sees.
    private static ICopyEndpoint[] Endpoints() =>
    [
        new LocalCopyEndpoint(),
        new AzureBlobCopyEndpoint(new StubCredentials(), new NoSecrets()),
        new S3CopyEndpoint(new NoSecrets()),
    ];

    [Theory]
    [InlineData("s3://bucket/export_orders", typeof(S3CopyEndpoint))]
    [InlineData("S3://Bucket/Prefix", typeof(S3CopyEndpoint))]
    [InlineData("abfss://fs@acct.dfs.core.windows.net/raw", typeof(AzureBlobCopyEndpoint))]
    [InlineData("https://acct.dfs.core.windows.net/fs/raw", typeof(AzureBlobCopyEndpoint))]
    [InlineData("C:\\data\\in", typeof(LocalCopyEndpoint))]
    [InlineData("/var/data/in", typeof(LocalCopyEndpoint))]
    [InlineData("\\\\server\\share\\in", typeof(LocalCopyEndpoint))]
    [InlineData("file:///c:/data/in", typeof(LocalCopyEndpoint))]
    public void First_matching_endpoint_is_the_right_one_for_the_scheme(string location, Type expected)
    {
        var selected = Array.Find(Endpoints(), e => e.CanHandle(location));
        Assert.NotNull(selected);
        Assert.IsType(expected, selected);
    }

    [Fact]
    public void Local_endpoint_does_not_claim_an_s3_location()
    {
        // The regression: local must NOT handle s3:// (it would prepend the CWD and never reach the S3 endpoint).
        Assert.False(new LocalCopyEndpoint().CanHandle("s3://bucket/prefix"));
        Assert.True(new S3CopyEndpoint(new NoSecrets()).CanHandle("s3://bucket/prefix"));
    }

    [Theory]
    // A flat key prefix stays verbatim (no forced trailing slash) so it matches S3's literal-prefix listing:
    // 'export_orders' selects 'export_orders_2021_...'.
    [InlineData("s3://elasticsearch-export-contoso/export_orders", "elasticsearch-export-contoso", "export_orders")]
    [InlineData("s3://bucket/a/b/c", "bucket", "a/b/c")]
    [InlineData("s3://bucket/folder/", "bucket", "folder")]
    [InlineData("s3://bucket", "bucket", "")]
    [InlineData("s3://bucket/", "bucket", "")]
    public void S3Location_parses_bucket_and_literal_prefix(string location, string bucket, string prefix)
    {
        var loc = S3CopyEndpoint.S3Location.Parse(location);
        Assert.Equal(bucket, loc.Bucket);
        Assert.Equal(prefix, loc.Prefix);
    }

    [Fact]
    public void S3Location_KeyFor_joins_prefix_and_relative_with_a_single_slash()
    {
        Assert.Equal("a/b/file.json", S3CopyEndpoint.S3Location.Parse("s3://bucket/a/b").KeyFor("file.json"));
        Assert.Equal("file.json", S3CopyEndpoint.S3Location.Parse("s3://bucket").KeyFor("/file.json"));
    }

    // A relative path can be an archive entry's own name; a '..' segment would name an object beside the target.
    [Theory]
    [InlineData("../file.json")]
    [InlineData("bundle/../../file.json")]
    [InlineData("bundle\\..\\..\\file.json")]
    [InlineData("..")]
    public void Object_store_targets_refuse_a_path_with_a_dot_dot_segment(string relativePath)
    {
        var refused = Assert.Throws<SqlFlowException>(() => CopyTargetPath.RequireContained(relativePath));
        Assert.Contains("escapes the target root", refused.Message, StringComparison.Ordinal);
        Assert.Throws<SqlFlowException>(() => S3CopyEndpoint.S3Location.Parse("s3://bucket/target").KeyFor(relativePath));
    }

    [Theory]
    [InlineData("a/..b/c")]
    [InlineData("a/b../c")]
    [InlineData(".../file.json")]
    [InlineData("./file.json")]
    public void Object_store_targets_keep_a_name_that_only_holds_dots(string relativePath)
        => Assert.Equal(relativePath, CopyTargetPath.RequireContained(relativePath));

    [Fact]
    public async Task Azure_blob_target_refuses_a_path_leaving_it_before_building_a_client()
    {
        // StubCredentials throws if a client were built, so the refusal must come from the name alone.
        var endpoint = new AzureBlobCopyEndpoint(new StubCredentials(), new NoSecrets());
        var target = new SqlFlow.Core.Copy.CopyEndpoint { Location = "abfss://fs@acct.dfs.core.windows.net/target" };

        await Assert.ThrowsAsync<SqlFlowException>(() =>
            endpoint.WriteAsync(target, "bundle/../../other/file.json", new byte[] { 1 }, overwrite: true, new byte[16], default));
        await Assert.ThrowsAsync<SqlFlowException>(() =>
            endpoint.TargetContentHashAsync(target, "../file.json", default));
    }

    [Fact]
    public void Md5FromETag_reads_a_single_part_etag_and_rejects_a_multipart_one()
    {
        // A plain 32-hex ETag is the object's MD5 (single-part upload): usable to skip an unchanged object.
        var md5 = S3CopyEndpoint.Md5FromETag("\"d41d8cd98f00b204e9800998ecf8427e\"");
        Assert.NotNull(md5);
        Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", Convert.ToHexString(md5!).ToLowerInvariant());

        // A multipart ETag (<hex>-<n>) is not an MD5, and blanks are unknown: report no hash so the engine hashes bytes.
        Assert.Null(S3CopyEndpoint.Md5FromETag("\"d41d8cd98f00b204e9800998ecf8427e-3\""));
        Assert.Null(S3CopyEndpoint.Md5FromETag("\"not-a-hash\""));
        Assert.Null(S3CopyEndpoint.Md5FromETag(""));
        Assert.Null(S3CopyEndpoint.Md5FromETag(null));
    }

    [Fact]
    public void PageObjects_reads_a_null_object_collection_as_an_empty_page()
    {
        // AWS SDK v4 leaves S3Objects null on a page with no objects (an empty bucket or prefix): the listing must yield
        // nothing rather than throw, so an empty source copies nothing and an empty target reads as "every file is new".
        Assert.Empty(S3CopyEndpoint.PageObjects(new ListObjectsV2Response { S3Objects = null }));

        var page = new ListObjectsV2Response { S3Objects = [new S3Object { Key = "export/orders_1.json" }] };
        Assert.Equal("export/orders_1.json", Assert.Single(S3CopyEndpoint.PageObjects(page)).Key);
    }

    [Fact]
    public void ToCopyItem_maps_a_listed_object_and_its_nullable_timestamp_and_size()
    {
        var listed = new S3Object
        {
            Key = "export/orders_1.json",
            LastModified = new DateTime(2026, 10, 1, 12, 30, 0, DateTimeKind.Utc),
            Size = 1234,
            ETag = "\"d41d8cd98f00b204e9800998ecf8427e\"",
        };

        var item = S3CopyEndpoint.ToCopyItem(listed, "orders_1.json", "orders_1.json");
        Assert.Equal("export/orders_1.json", item.AbsolutePath);
        Assert.Equal("orders_1.json", item.RelativePath);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 12, 30, 0, TimeSpan.Zero), item.Modified);
        Assert.Equal(1234, item.Size);
        Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", Convert.ToHexString(item.ContentHash!).ToLowerInvariant());

        // SDK v4 types LastModified and Size as nullable: an absent value maps to the CopyItem conventions every endpoint
        // shares (undated, which a modified window keeps; size -1), never to a fabricated timestamp or a zero size.
        var bare = S3CopyEndpoint.ToCopyItem(new S3Object { Key = "k.json" }, "k.json", "k.json");
        Assert.Null(bare.Modified);
        Assert.Equal(-1, bare.Size);
        Assert.Null(bare.ContentHash);
    }
}
