using System.Text;
using Azure;
using Azure.Identity;
using SqlFlow.Azure.Storage;
using SqlFlow.Core;
using Xunit;

namespace SqlFlow.Tests.Azure;

/// <summary>
/// Which endpoint an account is reached on: the Blob endpoint while it answers, the Data Lake endpoint when the network
/// refuses Blob (and the other way round), a private endpoint first, one decision per account, a refused request moved to
/// the other endpoint only when it was never processed, and a diagnosis naming both endpoints when neither answers.
/// </summary>
public sealed class AzureStorageEndpointSelectionTests
{
    private const string Container = "lake";

    private static FakeStorageAccount Account()
    {
        var account = new FakeStorageAccount("acct");
        account.CreateContainer(Container);
        account.PutFile(Container, "raw/a.csv", "id\n1\n");
        return account;
    }

    [Fact]
    public async Task Blob_is_used_while_both_endpoints_answer_and_neither_is_private()
    {
        var account = Account();
        var container = StorageHarness.Access(account).Container(StorageHarness.Location(account, Container));

        var props = await container.GetPropertiesAsync("raw/a.csv");

        Assert.Equal(5, props!.Length);
        Assert.Equal(AzureStorageEndpoint.Blob, await container.EndpointAsync());
        Assert.Equal(0, account.RequestsTo(account.DfsHost));
    }

    [Theory]
    [InlineData(EndpointBehavior.RejectNetwork)]
    [InlineData(EndpointBehavior.Unresolvable)]
    [InlineData(EndpointBehavior.Refused)]
    public async Task Dfs_is_used_when_this_host_cannot_reach_blob(EndpointBehavior blob)
    {
        var account = Account();
        account.Blob = blob;
        var logger = new ListLogger();
        var container = StorageHarness.Access(account, logger: logger).Container(StorageHarness.Location(account, Container));

        var props = await container.GetPropertiesAsync("raw/a.csv");

        Assert.Equal(5, props!.Length);
        Assert.Equal(AzureStorageEndpoint.Dfs, await container.EndpointAsync());
        // One probe of the Blob endpoint, never retried, and every request after it went to the Data Lake endpoint.
        Assert.Equal(1, account.RequestsTo(account.BlobHost));
        Assert.Contains(logger.Messages, m => m.Contains("reached over its dfs endpoint", StringComparison.Ordinal)
            && m.Contains("its blob endpoint", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Blob_is_used_when_this_host_cannot_reach_dfs_although_dfs_is_private()
    {
        var account = Account();
        account.Dfs = EndpointBehavior.RejectNetwork;
        var container = StorageHarness.Access(account, privateHosts: [account.DfsHost]).Container(StorageHarness.Location(account, Container));

        var props = await container.GetPropertiesAsync("raw/a.csv");

        Assert.Equal(5, props!.Length);
        Assert.Equal(AzureStorageEndpoint.Blob, await container.EndpointAsync());
        // The private endpoint was tried first, refused, and then Blob answered.
        Assert.Equal(account.DfsHost, account.Requests[0].Host);
        Assert.Equal(1, account.RequestsTo(account.DfsHost));
    }

    [Fact]
    public async Task A_private_dfs_endpoint_is_tried_first_and_blob_is_never_contacted()
    {
        var account = Account();
        var container = StorageHarness.Access(account, privateHosts: [account.DfsHost]).Container(StorageHarness.Location(account, Container));

        await container.GetPropertiesAsync("raw/a.csv");

        Assert.Equal(AzureStorageEndpoint.Dfs, await container.EndpointAsync());
        Assert.Equal(0, account.RequestsTo(account.BlobHost));
    }

    [Fact]
    public async Task Neither_endpoint_answering_names_both_and_is_detected_again_on_the_next_request()
    {
        var account = Account();
        account.Blob = EndpointBehavior.RejectNetwork;
        account.Dfs = EndpointBehavior.Unresolvable;
        var container = StorageHarness.Access(account, privateHosts: [account.DfsHost]).Container(StorageHarness.Location(account, Container));

        var failure = await Assert.ThrowsAsync<SqlFlowException>(() => container.GetPropertiesAsync("raw/a.csv"));

        Assert.Contains("Azure Storage account 'acct' cannot be reached from this host over either of its endpoints", failure.Message, StringComparison.Ordinal);
        Assert.Contains($"dfs (acct.dfs.core.windows.net, resolves to private {StorageHarness.PrivateAddress}): the host name did not resolve", failure.Message, StringComparison.Ordinal);
        Assert.Contains($"blob (acct.blob.core.windows.net, resolves to public {StorageHarness.PublicAddress}): the account's network rules refused this host (403 AuthorizationFailure)", failure.Message, StringComparison.Ordinal);
        Assert.Contains("private endpoint for the account's 'blob' or 'dfs' sub-resource", failure.Message, StringComparison.Ordinal);

        // The network is fixed (the Data Lake endpoint's DNS zone now resolves it): the next request detects again.
        account.Dfs = EndpointBehavior.Serve;
        Assert.Equal(5, (await container.GetPropertiesAsync("raw/a.csv"))!.Length);
        Assert.Equal(AzureStorageEndpoint.Dfs, await container.EndpointAsync());
    }

    [Fact]
    public async Task The_decision_is_made_once_per_account_and_shared_by_its_containers()
    {
        var account = Account();
        account.CreateContainer("other");
        account.Blob = EndpointBehavior.RejectNetwork;
        var access = StorageHarness.Access(account);

        await access.Container(StorageHarness.Location(account, Container)).GetPropertiesAsync("raw/a.csv");
        await access.Container(StorageHarness.Location(account, "other")).GetPropertiesAsync("missing.csv");
        await access.Container(StorageHarness.Location(account, Container)).GetPropertiesAsync("raw/a.csv");

        Assert.Equal(1, account.RequestsTo(account.BlobHost));
        Assert.Equal(1, account.Requests.Count(r => r.Host == account.DfsHost && r.Method == "HEAD" && r.Query.Contains("resource=filesystem", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Concurrent_first_requests_share_one_detection()
    {
        var account = Account();
        account.Blob = EndpointBehavior.RejectNetwork;
        var container = StorageHarness.Access(account).Container(StorageHarness.Location(account, Container));

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => container.GetPropertiesAsync("raw/a.csv")));

        Assert.Equal(1, account.RequestsTo(account.BlobHost));
    }

    [Theory]
    [InlineData("dfs", AzureStorageEndpoint.Dfs)]
    [InlineData("DFS", AzureStorageEndpoint.Dfs)]
    [InlineData("blob", AzureStorageEndpoint.Blob)]
    public async Task A_fixed_endpoint_is_used_without_probing(string setting, AzureStorageEndpoint expected)
    {
        var account = Account();
        var container = StorageHarness.Access(account, endpoint: setting).Container(StorageHarness.Location(account, Container));

        await container.GetPropertiesAsync("raw/a.csv");

        Assert.Equal(expected, await container.EndpointAsync());
        var other = expected == AzureStorageEndpoint.Dfs ? account.BlobHost : account.DfsHost;
        Assert.Equal(0, account.RequestsTo(other));
        Assert.Single(account.Requests);
    }

    [Fact]
    public async Task A_fixed_endpoint_never_moves_to_the_other()
    {
        var account = Account();
        account.Blob = EndpointBehavior.RejectNetwork;
        var container = StorageHarness.Access(account, endpoint: "blob").Container(StorageHarness.Location(account, Container));

        var refused = await Assert.ThrowsAsync<RequestFailedException>(() => container.GetPropertiesAsync("raw/a.csv"));

        Assert.Equal("AuthorizationFailure", refused.ErrorCode);
        Assert.Equal(AzureStorageEndpoint.Blob, AzureStorageFailures.EndpointOf(refused));
        Assert.Equal(0, account.RequestsTo(account.DfsHost));
    }

    [Fact]
    public async Task A_setting_it_does_not_take_fails_every_request_with_its_name()
    {
        var account = Account();
        var container = StorageHarness.Access(account, endpoint: "datalake").Container(StorageHarness.Location(account, Container));

        var failure = await Assert.ThrowsAsync<SqlFlowException>(() => container.GetPropertiesAsync("raw/a.csv"));

        Assert.Equal("SQLFLOW_AZURE_STORAGE_ENDPOINT is 'datalake'; it takes 'auto' (the default: whichever endpoint this host reaches), 'blob' or 'dfs'.", failure.Message);
        Assert.Empty(account.Requests);
    }

    [Fact]
    public async Task A_request_the_network_refuses_after_the_decision_moves_to_the_other_endpoint()
    {
        var account = Account();
        var logger = new ListLogger();
        var container = StorageHarness.Access(account, logger: logger).Container(StorageHarness.Location(account, Container));
        Assert.Equal(AzureStorageEndpoint.Blob, await container.EndpointAsync());

        // Public access is switched off while the host runs: Blob now refuses it, the Data Lake private endpoint does not.
        account.Blob = EndpointBehavior.RejectNetwork;
        var props = await container.GetPropertiesAsync("raw/a.csv");

        Assert.Equal(5, props!.Length);
        Assert.Equal(AzureStorageEndpoint.Dfs, await container.EndpointAsync());
        Assert.Contains(logger.Messages, m => m.Contains("moved from its blob endpoint to its dfs endpoint", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_listing_refused_before_its_first_entry_starts_over_on_the_other_endpoint()
    {
        var account = Account();
        var container = StorageHarness.Access(account).Container(StorageHarness.Location(account, Container));
        Assert.Equal(AzureStorageEndpoint.Blob, await container.EndpointAsync());

        account.Blob = EndpointBehavior.Unresolvable;
        var names = new List<string>();
        await foreach (var item in container.ListAsync("raw", recursive: true))
        {
            names.Add(item.Name);
        }

        Assert.Equal(["raw/a.csv"], names);
        Assert.Equal(AzureStorageEndpoint.Dfs, await container.EndpointAsync());
    }

    [Fact]
    public async Task A_request_the_service_may_have_processed_is_never_sent_again()
    {
        var account = Account();
        account.Blob = EndpointBehavior.ServerError;
        var container = StorageHarness.Access(account).Container(StorageHarness.Location(account, Container));

        // A 500 is an answer: the Blob endpoint is reachable and chosen, and its failure surfaces as itself.
        var failure = await Assert.ThrowsAsync<RequestFailedException>(() => container.GetPropertiesAsync("raw/a.csv"));

        Assert.Equal(500, failure.Status);
        Assert.Equal(AzureStorageEndpoint.Blob, AzureStorageFailures.EndpointOf(failure));
        Assert.Equal(0, account.RequestsTo(account.DfsHost));
    }

    [Fact]
    public async Task A_forward_only_upload_refused_by_the_network_is_not_sent_again()
    {
        var account = Account();
        var container = StorageHarness.Access(account).Container(StorageHarness.Location(account, Container));
        Assert.Equal(AzureStorageEndpoint.Blob, await container.EndpointAsync());
        account.Blob = EndpointBehavior.RejectNetwork;

        await using var content = new ForwardOnlyStream(Encoding.UTF8.GetBytes("payload"));
        var refused = await Assert.ThrowsAsync<RequestFailedException>(() => container.UploadAsync("raw/b.csv", content, overwrite: true));

        Assert.Equal("AuthorizationFailure", refused.ErrorCode);
        Assert.False(account.Exists(Container, "raw/b.csv"));
    }

    [Fact]
    public async Task A_seekable_upload_refused_by_the_network_is_sent_whole_to_the_other_endpoint()
    {
        var account = Account();
        var container = StorageHarness.Access(account).Container(StorageHarness.Location(account, Container));
        Assert.Equal(AzureStorageEndpoint.Blob, await container.EndpointAsync());
        account.Blob = EndpointBehavior.RejectNetwork;

        using var content = new MemoryStream(Encoding.UTF8.GetBytes("payload"));
        await container.UploadAsync("raw/b.csv", content, overwrite: true);

        Assert.Equal("payload", Encoding.UTF8.GetString(account.Content(Container, "raw/b.csv")!));
    }

    [Fact]
    public async Task A_credential_that_cannot_sign_in_is_reported_as_itself_and_nothing_is_remembered()
    {
        var account = Account();
        var access = StorageHarness.Access(account, credentials: new FakeCredentials(fail: true));
        var container = access.Container(StorageHarness.Location(account, Container));

        await Assert.ThrowsAsync<AuthenticationFailedException>(() => container.GetPropertiesAsync("raw/a.csv"));
        await Assert.ThrowsAsync<AuthenticationFailedException>(() => container.GetPropertiesAsync("raw/a.csv"));
    }

    [Fact]
    public async Task A_connection_string_for_a_custom_endpoint_has_only_its_blob_endpoint()
    {
        var account = Account();
        // An emulator's connection string: a custom Blob endpoint, and a key made up here (no line of the source
        // holds one).
        var emulator = "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;"
            + $"AccountKey={Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64))};"
            + "BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;";
        var location = StorageHarness.Location(account, Container);

        var auto = StorageHarness.Access(account).Container(location, AzureStorageAuth.FromConnectionString(emulator));
        Assert.Equal(AzureStorageEndpoint.Blob, await auto.EndpointAsync());
        Assert.Empty(account.Requests);

        var dfs = StorageHarness.Access(account, endpoint: "dfs").Container(location, AzureStorageAuth.FromConnectionString(emulator));
        var failure = await Assert.ThrowsAsync<SqlFlowException>(() => dfs.EndpointAsync());
        Assert.Contains("has no Data Lake endpoint", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_auth_never_renders_its_secret()
    {
        Assert.Equal("AccountKey", AzureStorageAuth.FromAccountKey("c2VjcmV0").ToString());
        Assert.Equal("SharedAccessSignature", AzureStorageAuth.FromSharedAccessSignature("?sv=1&sig=abc").ToString());
        Assert.Equal("ConnectionString", AzureStorageAuth.FromConnectionString("AccountName=a;AccountKey=b").ToString());
    }

    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.20.1.1", true)]
    [InlineData("192.168.4.5", true)]
    [InlineData("100.64.0.9", true)]
    [InlineData("fd00::1", true)]
    [InlineData("::ffff:10.1.2.3", true)]
    [InlineData("20.60.1.1", false)]
    [InlineData("52.239.1.1", false)]
    [InlineData("2603:1020::1", false)]
    public void Private_addresses_are_the_private_ranges(string address, bool expected)
        => Assert.Equal(expected, AzureStorageEndpointSelector.IsPrivate(System.Net.IPAddress.Parse(address)));

    /// <summary>A stream that can only be read forward, as a network body is.</summary>
    private sealed class ForwardOnlyStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;

        public override long Position
        {
            get => base.Position;
            set => throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
    }
}
