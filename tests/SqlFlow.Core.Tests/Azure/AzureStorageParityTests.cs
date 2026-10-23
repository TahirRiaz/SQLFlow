using System.Text;
using Azure;
using SqlFlow.Azure.Storage;
using SqlFlow.Core;
using Xunit;

namespace SqlFlow.Tests.Azure;

/// <summary>
/// The same operations over the Blob endpoint (through the Azure Blob SDK) and over the Data Lake endpoint (through the
/// storage layer's Data Lake client), against the same hierarchical-namespace account: every result must be the same.
/// The listings show the same files and the same directories (the Blob endpoint's directory-marker blobs and listing
/// prefixes read as the directories the Data Lake endpoint lists), reads return the same bytes, writes store the same
/// content hash, and the same requests are refused with the same statuses.
/// </summary>
public sealed class AzureStorageParityTests
{
    private const string Container = "lake";

    private static (FakeStorageAccount Account, AzureStorageContainer Container) Arrange(string endpoint)
    {
        var account = new FakeStorageAccount("acct");
        account.CreateContainer(Container);
        account.PutFile(Container, "raw/2026/01/a.csv", "a,1\n");
        account.PutFile(Container, "raw/2026/02/b.csv", "bb,2\n");
        account.PutFile(Container, "raw/readme.txt", "hello");
        account.PutDirectory(Container, "raw/empty");
        return (account, StorageHarness.Access(account, endpoint: endpoint).Container(StorageHarness.Location(account, Container)));
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

    [Theory]
    [InlineData("blob")]
    [InlineData("dfs")]
    public async Task Listings_show_the_same_files_and_directories(string endpoint)
    {
        var (_, container) = Arrange(endpoint);

        Assert.Equal(
            ["raw/2026/", "raw/2026/01/", "raw/2026/01/a.csv (4)", "raw/2026/02/", "raw/2026/02/b.csv (5)", "raw/empty/", "raw/readme.txt (5)"],
            await ListAsync(container, "raw/", recursive: true));
        Assert.Equal(["raw/2026/", "raw/empty/", "raw/readme.txt (5)"], await ListAsync(container, "raw", recursive: false));
        Assert.Equal(["raw/2026/01/a.csv (4)"], await ListAsync(container, "raw/2026/01/", recursive: false));
        Assert.Empty(await ListAsync(container, "raw/nothing-here/", recursive: true));
        Assert.Empty(await ListAsync(container, "raw/empty/", recursive: false));
    }

    [Theory]
    [InlineData("blob")]
    [InlineData("dfs")]
    public async Task Listed_files_carry_their_modification_time(string endpoint)
    {
        var (_, container) = Arrange(endpoint);

        await foreach (var item in container.ListAsync("raw/2026/01/", recursive: false))
        {
            Assert.NotNull(item.LastModified);
            Assert.Equal(2026, item.LastModified!.Value.Year);
        }
    }

    [Theory]
    [InlineData("blob")]
    [InlineData("dfs")]
    public async Task Properties_tell_a_file_from_a_directory_and_from_nothing(string endpoint)
    {
        var (account, container) = Arrange(endpoint);

        var file = await container.GetPropertiesAsync("raw/2026/01/a.csv");
        Assert.NotNull(file);
        Assert.False(file.IsDirectory);
        Assert.Equal(4, file.Length);
        Assert.Equal(account.StoredMd5(Container, "raw/2026/01/a.csv"), file.ContentHash);
        Assert.False(string.IsNullOrEmpty(file.ETag));

        var directory = await container.GetPropertiesAsync("raw/2026");
        Assert.NotNull(directory);
        Assert.True(directory.IsDirectory);

        Assert.Null(await container.GetPropertiesAsync("raw/missing.csv"));
    }

    [Theory]
    [InlineData("blob")]
    [InlineData("dfs")]
    public async Task Reads_return_the_same_bytes_whole_by_range_and_by_seeking(string endpoint)
    {
        var (_, container) = Arrange(endpoint);

        Assert.Equal("bb,2\n", Encoding.UTF8.GetString(await container.ReadAllBytesAsync("raw/2026/02/b.csv")));

        await using (var range = await container.OpenReadAsync("raw/2026/02/b.csv", 1, 3))
        {
            using var reader = new StreamReader(range);
            Assert.Equal("b,2", await reader.ReadToEndAsync());
        }

        await using var seekable = await container.OpenSeekableReadAsync("raw/2026/02/b.csv");
        Assert.True(seekable.CanSeek);
        Assert.Equal(5, seekable.Length);
        seekable.Seek(-2, SeekOrigin.End);
        var tail = new byte[2];
        await seekable.ReadExactlyAsync(tail);
        Assert.Equal("2\n", Encoding.UTF8.GetString(tail));
        seekable.Position = 0;
        using var whole = new MemoryStream();
        await seekable.CopyToAsync(whole);
        Assert.Equal("bb,2\n", Encoding.UTF8.GetString(whole.ToArray()));
    }

    [Theory]
    [InlineData("blob")]
    [InlineData("dfs")]
    public async Task A_missing_file_cannot_be_read(string endpoint)
    {
        var (_, container) = Arrange(endpoint);

        var failure = await Assert.ThrowsAsync<RequestFailedException>(() => container.OpenReadAsync("raw/missing.csv"));

        Assert.Equal(404, failure.Status);
    }

    [Theory]
    [InlineData("blob")]
    [InlineData("dfs")]
    public async Task An_upload_stores_its_content_and_content_hash(string endpoint)
    {
        var (account, container) = Arrange(endpoint);
        var content = Encoding.UTF8.GetBytes("id,name\n7,seven\n");
        var md5 = ContentHash.Md5(content);

        using (var stream = new MemoryStream(content))
        {
            await container.UploadAsync("landing/new/file.csv", stream, overwrite: true, md5);
        }

        Assert.Equal(content, account.Content(Container, "landing/new/file.csv"));
        Assert.Equal(md5, (await container.GetPropertiesAsync("landing/new/file.csv"))!.ContentHash);
        Assert.True((await container.GetPropertiesAsync("landing/new"))!.IsDirectory);
    }

    [Theory]
    [InlineData("blob")]
    [InlineData("dfs")]
    public async Task A_create_only_upload_refuses_an_existing_file_and_leaves_it_as_it_was(string endpoint)
    {
        var (account, container) = Arrange(endpoint);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("replacement"));
        var failure = await Assert.ThrowsAsync<RequestFailedException>(() => container.UploadAsync("raw/readme.txt", stream, overwrite: false));

        Assert.Equal(409, failure.Status);
        Assert.Equal("hello", Encoding.UTF8.GetString(account.Content(Container, "raw/readme.txt")!));
    }

    [Theory]
    [InlineData("blob")]
    [InlineData("dfs")]
    public async Task An_overwrite_replaces_the_file(string endpoint)
    {
        var (account, container) = Arrange(endpoint);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("bye"));
        await container.UploadAsync("raw/readme.txt", stream, overwrite: true);

        Assert.Equal("bye", Encoding.UTF8.GetString(account.Content(Container, "raw/readme.txt")!));
    }

    [Theory]
    [InlineData("blob")]
    [InlineData("dfs")]
    public async Task A_delete_reports_whether_something_was_there(string endpoint)
    {
        var (account, container) = Arrange(endpoint);

        Assert.True(await container.DeleteIfExistsAsync("raw/readme.txt"));
        Assert.False(account.Exists(Container, "raw/readme.txt"));
        Assert.False(await container.DeleteIfExistsAsync("raw/readme.txt"));
    }

    [Theory]
    [InlineData("blob")]
    [InlineData("dfs")]
    public async Task Creating_the_container_is_idempotent(string endpoint)
    {
        var (account, _) = Arrange(endpoint);
        var other = StorageHarness.Access(account, endpoint: endpoint).Container(StorageHarness.Location(account, "exports"));

        await other.CreateIfNotExistsAsync();
        await other.CreateIfNotExistsAsync();

        Assert.True(account.ContainerExists("exports"));
    }

    [Theory]
    [InlineData("blob")]
    [InlineData("dfs")]
    public async Task A_missing_container_fails_a_listing_with_404(string endpoint)
    {
        var (account, _) = Arrange(endpoint);
        var missing = StorageHarness.Access(account, endpoint: endpoint).Container(StorageHarness.Location(account, "nosuch"));

        var failure = await Assert.ThrowsAsync<RequestFailedException>(() => ListAsync(missing, "raw/", recursive: true));

        Assert.Equal(404, failure.Status);
        Assert.Null(await missing.GetPropertiesAsync("raw/a.csv"));
    }
}
