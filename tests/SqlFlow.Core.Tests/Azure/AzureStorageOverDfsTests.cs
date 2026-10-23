using System.IO.Compression;
using System.Text;
using SqlFlow.Acquire.Landing;
using SqlFlow.Azure;
using SqlFlow.Azure.Storage;
using SqlFlow.Copy;
using SqlFlow.Core;
using SqlFlow.Core.Copy;
using SqlFlow.Core.Model;
using Xunit;

namespace SqlFlow.Tests.Azure;

/// <summary>
/// Every store that reads or writes Azure Storage, on a network that reaches the account only over its Data Lake endpoint
/// (a private endpoint for the 'dfs' sub-resource, while the Blob endpoint resolves to the public address the account's
/// firewall refuses): file discovery and loads, landing, exports and copies work as they do over the Blob endpoint.
/// </summary>
public sealed class AzureStorageOverDfsTests
{
    private const string Container = "landing";

    private static (FakeStorageAccount Account, AzureStorageAccess Access) Arrange()
    {
        var account = new FakeStorageAccount("lake");
        account.Blob = EndpointBehavior.RejectNetwork;
        account.CreateContainer(Container);
        return (account, StorageHarness.Access(account, privateHosts: [account.DfsHost]));
    }

    private static string Url(string path) => $"abfss://{Container}@lake.dfs.core.windows.net/{path}";

    [Fact]
    public async Task File_discovery_lists_and_reads_files_over_dfs()
    {
        var (account, access) = Arrange();
        account.PutFile(Container, "wells/2026/01/a.csv", "id\n1\n");
        account.PutFile(Container, "wells/2026/02/b.csv", "id\n2\n");
        account.PutFile(Container, "wells/notes.txt", "n");
        var store = new AzureBlobFileStore(access);

        var flat = await store.ListAsync(Url("wells"), new FileDiscovery { Pattern = "*.csv", Recursive = true });
        Assert.Equal([Url("wells/2026/01/a.csv"), Url("wells/2026/02/b.csv")], flat.Select(f => f.Path));
        Assert.Equal(5, flat[0].Size);

        var oneLevel = await store.ListAsync(Url("wells/"), new FileDiscovery { Pattern = "*", Recursive = false });
        Assert.Equal([Url("wells/notes.txt")], oneLevel.Select(f => f.Path));

        var single = await store.ListAsync(Url("wells/2026/01/a.csv"), new FileDiscovery { Pattern = "*" });
        Assert.Equal("a.csv", Assert.Single(single).Name);

        // A location naming a directory without its trailing slash is the directory, not one empty file.
        var directory = await store.ListAsync(Url("wells/2026"), new FileDiscovery { Pattern = "*", Recursive = true });
        Assert.Equal(2, directory.Count);

        await using var stream = await store.OpenReadAsync(flat[1]);
        using var reader = new StreamReader(stream);
        Assert.Equal("id\n2\n", await reader.ReadToEndAsync());
        // The Data Lake endpoint resolves privately, so it was tried first and the refused Blob endpoint never contacted.
        Assert.Equal(0, account.RequestsTo(account.BlobHost));
    }

    [Fact]
    public async Task A_failed_read_names_the_endpoint_it_went_to()
    {
        var (_, access) = Arrange();
        var store = new AzureBlobFileStore(access);

        var failure = await Assert.ThrowsAsync<SqlFlowException>(() => store.OpenReadAsync(new FileRef { Path = Url("wells/missing.csv"), Name = "missing.csv" }));

        Assert.StartsWith($"Azure Storage request failed for 'lake/{Container}' over the dfs endpoint (status 404)", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Landing_writes_create_only_skips_unchanged_content_and_lists_files_only()
    {
        var (account, access) = Arrange();
        var store = new AzureRawLandingStore(access);
        var payload = Encoding.UTF8.GetBytes("{\"id\":1}");

        Assert.True(await store.PutAsync(Url("api/2026/page1.json"), payload, overwrite: false));
        Assert.True(await store.ExistsAsync(Url("api/2026/page1.json")));
        var refused = await Assert.ThrowsAsync<SqlFlowException>(() => store.PutAsync(Url("api/2026/page1.json"), payload, overwrite: false));
        Assert.Contains("already exists and overwrite is disabled", refused.Message, StringComparison.Ordinal);

        Assert.True(await store.PutAsync(Url("api/2026/page2.json"), payload, overwrite: true));
        Assert.Equal(ContentHash.Md5(payload), account.StoredMd5(Container, "api/2026/page2.json"));
        Assert.False(await store.PutAsync(Url("api/2026/page2.json"), payload, overwrite: true));
        Assert.True(await store.PutAsync(Url("api/2026/page2.json"), Encoding.UTF8.GetBytes("{\"id\":2}"), overwrite: true));

        Assert.Equal(["2026/page1.json", "2026/page2.json"], await store.ListNamesAsync(Url("api")));
        Assert.Empty(await store.ListNamesAsync(Url("nothing-landed-yet")));
    }

    [Fact]
    public async Task An_export_writes_measures_reads_zips_and_deletes_over_dfs()
    {
        var (account, access) = Arrange();
        var destination = new AzureBlobExportDestination(access);
        var location = $"abfss://exports@lake.dfs.core.windows.net/orders/2026/orders.csv";

        await using (var stream = await destination.OpenWriteAsync(location))
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes("id,total\n1,9.5\n"));
        }

        Assert.True(account.ContainerExists("exports"));
        Assert.Equal(15, await destination.GetSizeAsync(location));
        await using (var read = await destination.OpenReadAsync(location))
        {
            using var reader = new StreamReader(read);
            Assert.Equal("id,total\n1,9.5\n", await reader.ReadToEndAsync());
        }

        var zipped = await destination.ZipAsync(location);
        Assert.Equal("abfss://exports@lake.dfs.core.windows.net/orders/2026/orders.zip", zipped);
        Assert.False(account.Exists("exports", "orders/2026/orders.csv"));
        using (var archive = new ZipArchive(new MemoryStream(account.Content("exports", "orders/2026/orders.zip")!)))
        {
            Assert.Equal("orders.csv", Assert.Single(archive.Entries).Name);
        }

        await destination.DeleteIfExistsAsync(zipped);
        Assert.Equal(0, await destination.GetSizeAsync(zipped));
    }

    [Fact]
    public async Task A_copy_lists_with_stored_hashes_writes_stamped_files_and_reads_target_hashes()
    {
        var (account, access) = Arrange();
        account.PutFile(Container, "vendor/in/a.json", "{\"a\":1}");
        account.PutFile(Container, "vendor/in/sub/b.json", "{\"b\":2}");
        account.PutFile(Container, "vendor/in/skip.txt", "x");
        var endpoint = new AzureBlobCopyEndpoint(access, new LiteralSecrets());
        var source = new CopyEndpoint { Location = Url("vendor/in"), Pattern = "*.json", Recursive = true };
        var target = new CopyEndpoint { Location = Url("vendor/out") };

        var items = new List<CopyItem>();
        await foreach (var item in endpoint.ListAsync(source, CopyModifiedWindow.Unbounded, default))
        {
            items.Add(item);
        }

        Assert.Equal(["a.json", "sub/b.json"], items.Select(i => i.RelativePath));
        // The Data Lake listing carries no hashes, so each matched file's stored hash came from its properties: the
        // engine can still skip an unchanged file without downloading it.
        Assert.Equal(ContentHash.Md5(Encoding.UTF8.GetBytes("{\"a\":1}")), items[0].ContentHash);

        var bytes = await endpoint.ReadAsync(source, items[1].AbsolutePath, default);
        var written = await endpoint.WriteAsync(target, "sub/b.json", bytes, overwrite: true, ContentHash.Md5(bytes), default);
        Assert.Equal(Url("vendor/out/sub/b.json"), written);

        var index = await endpoint.TargetHashIndexAsync(target, default);
        Assert.True(index.ContainsKey("sub/b.json"));
        Assert.Equal(ContentHash.Md5(bytes), await endpoint.TargetContentHashAsync(target, "sub/b.json", default));
        Assert.Null(await endpoint.TargetContentHashAsync(target, "sub/missing.json", default));

        var refused = await Assert.ThrowsAsync<SqlFlowException>(() => endpoint.WriteAsync(target, "sub/b.json", bytes, overwrite: false, ContentHash.Md5(bytes), default));
        Assert.Contains("already exists and overwrite is disabled", refused.Message, StringComparison.Ordinal);
    }
}
