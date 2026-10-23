using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Azure;
using Azure.Core.Pipeline;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using SqlFlow.Azure;
using SqlFlow.Azure.Storage;
using SqlFlow.Core;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Live proof, against a real ADLS Gen2 account, that the storage layer gives the same results over the Blob endpoint and
/// over the Data Lake endpoint, that an endpoint this host cannot reach is replaced by the other one, and that an account
/// key, a shared access signature and a connection string reach both endpoints. Runs only when
/// <c>SQLFLOW_AZTEST_STORAGE_ACCOUNT</c> names the account (the key tests also need <c>SQLFLOW_AZTEST_STORAGE_KEY</c>);
/// otherwise skips. Every test works in a container of its own, named <c>sqlflow-live-&lt;guid&gt;</c>, and deletes it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AzureStorageLiveTests
{
    private static readonly byte[] A = Encoding.UTF8.GetBytes("a,1\n");
    private static readonly byte[] B = Encoding.UTF8.GetBytes("bb,22\n");

    private static AzureStorageAccess Access(string? endpoint, HttpPipelineTransport? transport = null, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
        => new(
            new AzureCredentialFactory(),
            new AzureStorageAccessOptions
            {
                EndpointSetting = endpoint,
                Transport = transport,
                ResolveHost = resolve ?? Dns.GetHostAddressesAsync,
            },
            logger: null);

    private static AzureBlobLocation Location(string account, string container)
        => AzureBlobLocation.Parse($"abfss://{container}@{account}.dfs.core.windows.net/");

    private static string NewContainer() => $"sqlflow-live-{Guid.NewGuid():N}";

    [SkippableFact]
    public async Task Every_operation_gives_the_same_result_over_both_endpoints()
    {
        var account = AzureTestEnv.RequireStorageAccount();
        var transcripts = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var endpoint in new[] { "blob", "dfs" })
        {
            var name = NewContainer();
            await using var cleanup = new ContainerCleanup(account, name);
            var container = Access(endpoint).Container(Location(account, name));
            transcripts[endpoint] = await ScriptAsync(container);
            Assert.Equal(endpoint == "dfs" ? AzureStorageEndpoint.Dfs : AzureStorageEndpoint.Blob, await container.EndpointAsync());
        }

        Assert.Equal(transcripts["blob"], transcripts["dfs"]);
    }

    [SkippableTheory]
    [InlineData("blob", AzureStorageEndpoint.Dfs)]
    [InlineData("dfs", AzureStorageEndpoint.Blob)]
    public async Task An_endpoint_this_host_cannot_reach_is_replaced_by_the_other(string blocked, AzureStorageEndpoint expected)
    {
        var account = AzureTestEnv.RequireStorageAccount();
        var blockedHost = $"{account}.{blocked}.core.windows.net";
        using var handler = new UnresolvableHostHandler(blockedHost);
        // The blocked endpoint is made to look private, so detection tries it first and has to fall back.
        var access = Access(
            endpoint: null,
            new HttpClientTransport(handler),
            async (host, ct) => host.Equals(blockedHost, StringComparison.OrdinalIgnoreCase)
                ? [IPAddress.Parse("10.1.2.3")]
                : await Dns.GetHostAddressesAsync(host, ct));

        var name = NewContainer();
        await using var cleanup = new ContainerCleanup(account, name);
        var container = access.Container(Location(account, name));
        var transcript = await ScriptAsync(container);

        Assert.Equal(expected, await container.EndpointAsync());
        Assert.True(handler.Refused > 0, "Detection never tried the blocked endpoint.");
        Assert.Contains(transcript, line => line.StartsWith("a.csv: ", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task An_account_key_a_signature_and_a_connection_string_reach_both_endpoints()
    {
        var account = AzureTestEnv.RequireStorageAccount();
        var key = AzureTestEnv.RequireStorageKey();
        var service = new BlobServiceClient(new Uri($"https://{account}.blob.core.windows.net"), new StorageSharedKeyCredential(account, key));
        var signature = service.GenerateAccountSasUri(AccountSasPermissions.All, DateTimeOffset.UtcNow.AddHours(1), AccountSasResourceTypes.All).Query;
        var connectionString = $"DefaultEndpointsProtocol=https;AccountName={account};AccountKey={key};EndpointSuffix=core.windows.net";

        foreach (var endpoint in new[] { "blob", "dfs" })
        {
            foreach (var auth in new[]
            {
                AzureStorageAuth.FromAccountKey(key),
                AzureStorageAuth.FromSharedAccessSignature(signature),
                AzureStorageAuth.FromConnectionString(connectionString),
            })
            {
                var name = NewContainer();
                await using var cleanup = new ContainerCleanup(account, name);
                var container = Access(endpoint).Container(Location(account, name), auth);

                await container.CreateIfNotExistsAsync();
                await UploadAsync(container, "data/a.csv", A, overwrite: false, ContentHash.Md5(A));
                Assert.Equal(A, await container.ReadAllBytesAsync("data/a.csv"));
                Assert.Equal(ContentHash.Md5(A), (await container.GetPropertiesAsync("data/a.csv"))!.ContentHash);
                Assert.Equal(["data/", "data/a.csv (4)"], await ListAsync(container, string.Empty, recursive: true));
                Assert.True(await container.DeleteIfExistsAsync("data/a.csv"));
            }
        }
    }

    /// <summary>
    /// Every operation the storage layer offers, with its observable results written down: the transcripts of the two
    /// endpoints must be identical.
    /// </summary>
    private static async Task<List<string>> ScriptAsync(AzureStorageContainer container)
    {
        var transcript = new List<string>();
        await container.CreateIfNotExistsAsync();
        await container.CreateIfNotExistsAsync();

        await UploadAsync(container, "data/2026/01/a.csv", A, overwrite: false, ContentHash.Md5(A));
        await UploadAsync(container, "data/2026/02/b.csv", B, overwrite: true, ContentHash.Md5(B));
        await UploadAsync(container, "data/readme.txt", Encoding.UTF8.GetBytes("hello"), overwrite: true, contentHash: null);

        // A streamed write larger than one buffer, written in pieces that do not line up with it.
        var big = new byte[(5 * 1024 * 1024) + 3];
        for (var i = 0; i < big.Length; i++)
        {
            big[i] = (byte)(i % 251);
        }

        await using (var stream = await container.OpenWriteAsync("out/big.bin", bufferSize: 4 * 1024 * 1024))
        {
            for (var offset = 0; offset < big.Length; offset += 1_000_003)
            {
                await stream.WriteAsync(big.AsMemory(offset, Math.Min(1_000_003, big.Length - offset)));
            }
        }

        try
        {
            await UploadAsync(container, "data/readme.txt", Encoding.UTF8.GetBytes("again"), overwrite: false, contentHash: null);
            transcript.Add("create-only over an existing file: written");
        }
        catch (RequestFailedException ex)
        {
            transcript.Add($"create-only over an existing file: {ex.Status}");
        }

        transcript.Add("recursive: " + string.Join(" | ", await ListAsync(container, string.Empty, recursive: true)));
        transcript.Add("data, one level: " + string.Join(" | ", await ListAsync(container, "data", recursive: false)));
        transcript.Add("data/2026/, one level: " + string.Join(" | ", await ListAsync(container, "data/2026/", recursive: false)));
        transcript.Add("missing directory: " + string.Join(" | ", await ListAsync(container, "nothing-here", recursive: true)));

        var a = (await container.GetPropertiesAsync("data/2026/01/a.csv"))!;
        transcript.Add($"a.csv: {a.Length} bytes, directory {a.IsDirectory}, hash stored {a.ContentHash is not null && a.ContentHash.AsSpan().SequenceEqual(ContentHash.Md5(A))}");
        transcript.Add($"data/2026: directory {(await container.GetPropertiesAsync("data/2026"))!.IsDirectory}");
        transcript.Add($"big.bin: {(await container.GetPropertiesAsync("out/big.bin"))!.Length} bytes");
        transcript.Add($"missing file has properties: {await container.GetPropertiesAsync("data/none.csv") is not null}");

        transcript.Add("a.csv reads: " + Encoding.UTF8.GetString(await container.ReadAllBytesAsync("data/2026/01/a.csv")));
        await using (var range = await container.OpenReadAsync("data/2026/02/b.csv", 1, 4))
        {
            using var reader = new StreamReader(range);
            transcript.Add("b.csv bytes 1-4: " + await reader.ReadToEndAsync());
        }

        transcript.Add($"big.bin reads back whole: {(await container.ReadAllBytesAsync("out/big.bin")).AsSpan().SequenceEqual(big)}");
        await using (var across = await container.OpenReadAsync("out/big.bin", (4 * 1024 * 1024) - 2, 5))
        {
            using var copy = new MemoryStream();
            await across.CopyToAsync(copy);
            transcript.Add($"big.bin range across the part boundary: {copy.ToArray().AsSpan().SequenceEqual(big.AsSpan((4 * 1024 * 1024) - 2, 5))}");
        }

        await using (var seekable = await container.OpenSeekableReadAsync("out/big.bin"))
        {
            seekable.Seek(-10, SeekOrigin.End);
            var tail = new byte[10];
            await seekable.ReadExactlyAsync(tail);
            seekable.Position = 0;
            var head = new byte[16];
            await seekable.ReadExactlyAsync(head);
            transcript.Add($"big.bin seeks: length {seekable.Length}, tail {tail.AsSpan().SequenceEqual(big.AsSpan(big.Length - 10))}, head {head.AsSpan().SequenceEqual(big.AsSpan(0, 16))}");
        }

        try
        {
            await container.ReadAllBytesAsync("data/none.csv");
            transcript.Add("missing file reads: yes");
        }
        catch (RequestFailedException ex)
        {
            transcript.Add($"missing file reads: {ex.Status}");
        }

        transcript.Add($"delete: {await container.DeleteIfExistsAsync("data/readme.txt")}, then {await container.DeleteIfExistsAsync("data/readme.txt")}");
        transcript.Add("after delete: " + string.Join(" | ", await ListAsync(container, "data", recursive: false)));
        return transcript;
    }

    private static async Task UploadAsync(AzureStorageContainer container, string path, byte[] content, bool overwrite, byte[]? contentHash)
    {
        using var stream = new MemoryStream(content, writable: false);
        await container.UploadAsync(path, stream, overwrite, contentHash);
    }

    private static async Task<List<string>> ListAsync(AzureStorageContainer container, string prefix, bool recursive)
    {
        var entries = new List<string>();
        await foreach (var item in container.ListAsync(prefix, recursive))
        {
            entries.Add(item.IsDirectory ? $"{item.Name}/" : $"{item.Name} ({item.Length})");
        }

        return entries;
    }

    /// <summary>Deletes the test's container, whatever the test did.</summary>
    private sealed class ContainerCleanup(string account, string container) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            var service = new BlobServiceClient(new Uri($"https://{account}.blob.core.windows.net"), new AzureCredentialFactory().Create());
            await service.GetBlobContainerClient(container).DeleteIfExistsAsync();
        }
    }

    /// <summary>Fails every request to one host as a name that does not resolve, and passes the rest to the network.</summary>
    private sealed class UnresolvableHostHandler(string host) : DelegatingHandler(new SocketsHttpHandler())
    {
        private int _refused;

        public int Refused => _refused;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (string.Equals(request.RequestUri!.Host, host, StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _refused);
                throw new HttpRequestException(HttpRequestError.NameResolutionError, $"No such host is known. ({host})", new SocketException((int)SocketError.HostNotFound));
            }

            return base.SendAsync(request, cancellationToken);
        }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            => SendAsync(request, cancellationToken).GetAwaiter().GetResult();
    }
}
