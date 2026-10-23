using System.Security.Cryptography;
using System.Text;
using Azure;
using SqlFlow.Azure.Storage;
using SqlFlow.Core;
using Xunit;

namespace SqlFlow.Tests.Azure;

/// <summary>
/// The Data Lake endpoint's own mechanics behind the results it shares with the Blob endpoint: paged listings, a read that
/// resumes a dropped connection pinned to the file's version, a seekable read that fetches ranges, uploads appended in
/// parts and flushed with their content hash, cleanup of an upload that fails, a write stream that commits on flush and
/// dispose, paths escaped per segment, and each way a request authenticates.
/// </summary>
public sealed class DfsStorageTests
{
    private const string Container = "lake";

    private static (FakeStorageAccount Account, AzureStorageContainer Container) Arrange(AzureStorageAuth? auth = null)
    {
        var account = new FakeStorageAccount("acct");
        account.CreateContainer(Container);
        var access = StorageHarness.Access(account, endpoint: "dfs");
        var location = StorageHarness.Location(account, Container);
        return (account, auth is null ? access.Container(location) : access.Container(location, auth));
    }

    [Fact]
    public async Task A_listing_follows_its_continuation_across_pages()
    {
        var (account, container) = Arrange();
        account.DfsPageSize = 2;
        for (var i = 0; i < 7; i++)
        {
            account.PutFile(Container, $"raw/f{i}.csv", "x");
        }

        var names = new List<string>();
        await foreach (var item in container.ListAsync("raw", recursive: false))
        {
            names.Add(item.Name);
        }

        Assert.Equal(Enumerable.Range(0, 7).Select(i => $"raw/f{i}.csv"), names);
        Assert.Equal(4, account.Requests.Count(r => r.Method == "GET"));
        Assert.Contains(account.Requests, r => r.Query.Contains("continuation=6", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_listing_carries_no_content_hash_and_says_so()
    {
        var (account, container) = Arrange();
        account.PutFile(Container, "raw/a.csv", "x");

        await foreach (var item in container.ListAsync(string.Empty, recursive: true))
        {
            Assert.Null(item.ContentHash);
            Assert.False(item.ContentHashListed);
        }
    }

    [Fact]
    public async Task A_read_resumes_a_dropped_connection_from_the_byte_it_reached()
    {
        var (account, container) = Arrange();
        account.PutFile(Container, "raw/a.csv", "0123456789abcdef");
        account.BreakNextReadAfter = 5;

        var content = await container.ReadAllBytesAsync("raw/a.csv");

        Assert.Equal("0123456789abcdef", Encoding.UTF8.GetString(content));
        var resumed = account.Requests.Last(r => r.Method == "GET");
        Assert.Equal("bytes=5-15", resumed.Headers["Range"]);
        Assert.True(resumed.Headers.ContainsKey("If-Match"));
    }

    [Fact]
    public async Task A_seekable_read_fetches_its_buffer_by_range_and_fails_if_the_file_changes()
    {
        var (account, container) = Arrange();
        account.PutFile(Container, "raw/a.parquet", "0123456789");

        await using var stream = await container.OpenSeekableReadAsync("raw/a.parquet", bufferSize: 4);
        Assert.Equal(10, stream.Length);
        stream.Seek(-3, SeekOrigin.End);
        var footer = new byte[3];
        await stream.ReadExactlyAsync(footer);
        Assert.Equal("789", Encoding.UTF8.GetString(footer));
        Assert.Contains(account.Requests, r => r.Method == "GET" && r.Headers.GetValueOrDefault("Range") == "bytes=7-9");

        account.Modify(Container, "raw/a.parquet", Encoding.UTF8.GetBytes("changed!!!"));
        stream.Position = 0;
        var failure = await Assert.ThrowsAsync<RequestFailedException>(() => stream.ReadAsync(new byte[4]).AsTask());
        Assert.Equal(412, failure.Status);
    }

    [Fact]
    public async Task A_range_must_have_a_length()
    {
        var (account, container) = Arrange();
        account.PutFile(Container, "raw/a.csv", "x");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => container.OpenReadAsync("raw/a.csv", 0, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => container.OpenReadAsync("raw/a.csv", -1, 1));
    }

    [Fact]
    public async Task A_large_upload_is_appended_in_parts_at_their_positions_then_flushed_with_its_hash()
    {
        var (account, container) = Arrange();
        var content = new byte[(4 * 1024 * 1024) + 1234];
        Random.Shared.NextBytes(content);
        var md5 = ContentHash.Md5(content);

        using (var stream = new MemoryStream(content))
        {
            await container.UploadAsync("raw/big.bin", stream, overwrite: true, md5);
        }

        Assert.Equal(content, account.Content(Container, "raw/big.bin"));
        Assert.Equal(md5, account.StoredMd5(Container, "raw/big.bin"));
        var appends = account.Requests.Where(r => r.Query.Contains("action=append", StringComparison.Ordinal)).Select(r => r.Query).ToList();
        Assert.Equal(2, appends.Count);
        Assert.Contains("position=0", appends[0], StringComparison.Ordinal);
        Assert.Contains("position=4194304", appends[1], StringComparison.Ordinal);
        var flush = account.Requests.Single(r => r.Query.Contains("action=flush", StringComparison.Ordinal));
        Assert.Contains($"position={content.Length}", flush.Query, StringComparison.Ordinal);
        Assert.Contains("close=true", flush.Query, StringComparison.Ordinal);
        Assert.Equal(Convert.ToBase64String(md5), flush.Headers["x-ms-content-md5"]);
    }

    [Fact]
    public async Task An_empty_upload_creates_an_empty_file()
    {
        var (account, container) = Arrange();

        using (var stream = new MemoryStream())
        {
            await container.UploadAsync("raw/empty.csv", stream, overwrite: false, ContentHash.Md5([]));
        }

        Assert.Empty(account.Content(Container, "raw/empty.csv")!);
        Assert.DoesNotContain(account.Requests, r => r.Query.Contains("action=append", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_upload_that_fails_after_its_create_removes_what_it_created()
    {
        var (account, container) = Arrange();
        account.FailAppends = (400, "InvalidInput");

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("payload"));
        var failure = await Assert.ThrowsAsync<RequestFailedException>(() => container.UploadAsync("raw/a.csv", stream, overwrite: false));

        Assert.Equal(400, failure.Status);
        Assert.Equal("InvalidInput", failure.ErrorCode);
        Assert.False(account.Exists(Container, "raw/a.csv"));

        // Nothing is left to refuse the retry of a create-only write.
        account.FailAppends = null;
        stream.Position = 0;
        await container.UploadAsync("raw/a.csv", stream, overwrite: false);
        Assert.Equal("payload", Encoding.UTF8.GetString(account.Content(Container, "raw/a.csv")!));
    }

    [Fact]
    public async Task A_write_stream_commits_on_flush_and_on_dispose()
    {
        var (account, container) = Arrange();

        await using (var stream = await container.OpenWriteAsync("raw/out.csv", bufferSize: 4))
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes("id,v\n"));
            await stream.FlushAsync();
            Assert.Equal("id,v\n", Encoding.UTF8.GetString(account.Content(Container, "raw/out.csv")!));

            stream.Write(Encoding.UTF8.GetBytes("1,a\n2,b\n"));
        }

        Assert.Equal("id,v\n1,a\n2,b\n", Encoding.UTF8.GetString(account.Content(Container, "raw/out.csv")!));
        var flushes = account.Requests.Where(r => r.Query.Contains("action=flush", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, flushes.Count);
        Assert.Contains("close=true", flushes[^1].Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_write_stream_whose_append_failed_refuses_more_and_disposes_quietly()
    {
        var (account, container) = Arrange();
        var stream = await container.OpenWriteAsync("raw/out.csv", bufferSize: 4);
        account.FailAppends = (400, "InvalidInput");

        await Assert.ThrowsAsync<RequestFailedException>(() => stream.WriteAsync(Encoding.UTF8.GetBytes("12345678")).AsTask());
        await Assert.ThrowsAsync<IOException>(() => stream.WriteAsync(Encoding.UTF8.GetBytes("x")).AsTask());
        await stream.DisposeAsync();

        Assert.DoesNotContain(account.Requests, r => r.Query.Contains("action=flush", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_path_is_escaped_one_segment_at_a_time()
    {
        var (account, container) = Arrange();
        const string path = "raw/dir with space/résumé #1 100%.csv";

        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes("x")))
        {
            await container.UploadAsync(path, stream, overwrite: true);
        }

        Assert.True(account.Exists(Container, path));
        Assert.Equal("x", Encoding.UTF8.GetString(await container.ReadAllBytesAsync(path)));
        Assert.Contains(account.Requests, r => r.Path == "/lake/raw/dir%20with%20space/r%C3%A9sum%C3%A9%20%231%20100%25.csv");

        var names = new List<string>();
        await foreach (var item in container.ListAsync("raw/dir with space", recursive: false))
        {
            names.Add(item.Name);
        }

        Assert.Equal([path], names);
    }

    [Fact]
    public async Task Requests_carry_the_bearer_token_and_the_service_version()
    {
        var (account, container) = Arrange();

        await container.GetPropertiesAsync("raw/a.csv");

        var request = Assert.Single(account.Requests);
        Assert.Equal("Bearer test-token", request.Headers["Authorization"]);
        Assert.Equal(DfsRestClient.ServiceVersion, request.Headers["x-ms-version"]);
    }

    [Fact]
    public async Task A_shared_access_signature_is_sent_once_on_each_attempt_and_never_with_a_token()
    {
        var (account, container) = Arrange(AzureStorageAuth.FromSharedAccessSignature("?sv=2025-01-05&sr=c&sp=rl&sig=c2lnbmF0dXJl%3D"));
        account.PutFile(Container, "raw/a.csv", "x");
        account.BusyOnceHost = account.DfsHost;

        Assert.NotNull(await container.GetPropertiesAsync("raw/a.csv"));

        Assert.Equal(2, account.Requests.Count);
        Assert.All(account.Requests, r =>
        {
            Assert.Equal(1, CountOf(r.Query, "sig=c2lnbmF0dXJl%3D"));
            Assert.False(r.Headers.ContainsKey("Authorization"));
        });
    }

    [Fact]
    public async Task An_account_key_signs_an_account_signature_and_the_key_never_travels()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var (account, container) = Arrange(AzureStorageAuth.FromAccountKey(key));
        account.PutFile(Container, "raw/a.csv", "x");

        Assert.NotNull(await container.GetPropertiesAsync("raw/a.csv"));

        var request = Assert.Single(account.Requests);
        // An account signature for the Blob service, over the service, containers and files: what the key can do.
        Assert.Contains("ss=b", request.Query, StringComparison.Ordinal);
        Assert.Contains("srt=sco", request.Query, StringComparison.Ordinal);
        Assert.Contains("sig=", request.Query, StringComparison.Ordinal);
        Assert.False(request.Headers.ContainsKey("Authorization"));
        Assert.DoesNotContain(Uri.EscapeDataString(key), request.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_connection_string_with_a_key_reaches_the_dfs_twin_of_its_blob_endpoint()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var (account, container) = Arrange(AzureStorageAuth.FromConnectionString(
            $"DefaultEndpointsProtocol=https;AccountName=acct;AccountKey={key};EndpointSuffix=core.windows.net"));
        account.PutFile(Container, "raw/a.csv", "x");

        Assert.NotNull(await container.GetPropertiesAsync("raw/a.csv"));

        var request = Assert.Single(account.Requests);
        Assert.Equal(account.DfsHost, request.Host);
        Assert.Contains("sig=", request.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_connection_string_with_a_signature_sends_that_signature()
    {
        var (account, container) = Arrange(AzureStorageAuth.FromConnectionString(
            "BlobEndpoint=https://acct.blob.core.windows.net/;SharedAccessSignature=sv=2025-01-05&ss=b&srt=co&sp=rl&sig=YWJj%3D"));
        account.PutFile(Container, "raw/a.csv", "x");

        Assert.NotNull(await container.GetPropertiesAsync("raw/a.csv"));

        var request = Assert.Single(account.Requests);
        Assert.Equal(account.DfsHost, request.Host);
        Assert.Equal(1, CountOf(request.Query, "sig=YWJj%3D"));
    }

    [Fact]
    public async Task An_error_carries_the_service_code_and_message()
    {
        var (account, container) = Arrange();
        account.PutFile(Container, "raw/a.csv", "x");

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("y"));
        var failure = await Assert.ThrowsAsync<RequestFailedException>(() => container.UploadAsync("raw/a.csv", stream, overwrite: false));

        Assert.Equal(409, failure.Status);
        Assert.Equal("PathAlreadyExists", failure.ErrorCode);
        Assert.Contains("The specified path already exists.", failure.Message, StringComparison.Ordinal);
        Assert.Equal(AzureStorageEndpoint.Dfs, AzureStorageFailures.EndpointOf(failure));
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
