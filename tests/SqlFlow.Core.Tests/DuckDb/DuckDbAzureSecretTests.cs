using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Model;
using SqlFlow.DuckDb;
using Xunit;

namespace SqlFlow.Tests.DuckDb;

/// <summary>
/// The DuckDB Azure secret: the CREATE SECRET text per auth mode (injection-safe), and the decision logic that
/// auto-generates it only when SQLFlow is managing cloud auth (a provider is wired, the operator did not opt out
/// or declare their own secret, and the location is Azure storage). Pure - no native libduckdb, no live Azure.
/// </summary>
public sealed class DuckDbAzureSecretTests
{
    private sealed class FakeProvider(AzureStorageCredential? credential) : ICloudCredentialProvider
    {
        public AzureStorageCredential? ResolveAzureStorage(string location) => credential;
    }

    private static AzureStorageCredential Cred(CloudAuthMode mode, string account = "acct", string? tenant = null, string? client = null, string? secret = null, bool runningInAzure = false)
        => new() { AccountName = account, Mode = mode, TenantId = tenant, ClientId = client, ClientSecret = secret, RunningInAzure = runningInAzure };

    private static SourceSpec Source(string? location, params (string Key, string? Value)[] options)
        => new() { Type = "duckdb", Location = location, Options = options.ToDictionary(o => o.Key, o => o.Value, StringComparer.OrdinalIgnoreCase) };

    [Fact]
    public void DefaultChain_OffCloud_ExcludesManagedIdentity()
        => Assert.Equal(
            "CREATE OR REPLACE SECRET sqlflow_azure (TYPE AZURE, PROVIDER credential_chain, CHAIN 'cli;env', ACCOUNT_NAME 'acct')",
            DuckDbAzureSecret.CreateStatement(Cred(CloudAuthMode.DefaultChain, runningInAzure: false)));

    [Fact]
    public void DefaultChain_InAzure_IncludesManagedIdentity()
        => Assert.Equal(
            "CREATE OR REPLACE SECRET sqlflow_azure (TYPE AZURE, PROVIDER credential_chain, CHAIN 'managed_identity;cli;env', ACCOUNT_NAME 'acct')",
            DuckDbAzureSecret.CreateStatement(Cred(CloudAuthMode.DefaultChain, runningInAzure: true)));

    [Fact]
    public void ManagedIdentity_SystemAssigned_Statement()
        => Assert.Equal(
            "CREATE OR REPLACE SECRET sqlflow_azure (TYPE AZURE, PROVIDER credential_chain, CHAIN 'managed_identity', ACCOUNT_NAME 'acct')",
            DuckDbAzureSecret.CreateStatement(Cred(CloudAuthMode.ManagedIdentity)));

    [Fact]
    public void ManagedIdentity_UserAssigned_Throws()
        => Assert.Throws<SqlFlowException>(() =>
            DuckDbAzureSecret.CreateStatement(Cred(CloudAuthMode.ManagedIdentity, client: "user-assigned-id")));

    [Fact]
    public void AzureCli_Statement()
        => Assert.Equal(
            "CREATE OR REPLACE SECRET sqlflow_azure (TYPE AZURE, PROVIDER credential_chain, CHAIN 'cli', ACCOUNT_NAME 'acct')",
            DuckDbAzureSecret.CreateStatement(Cred(CloudAuthMode.AzureCli)));

    [Fact]
    public void ServicePrincipal_Statement_IncludesMaterial()
        => Assert.Equal(
            "CREATE OR REPLACE SECRET sqlflow_azure (TYPE AZURE, PROVIDER service_principal, TENANT_ID 'tid', CLIENT_ID 'cid', CLIENT_SECRET 'shh', ACCOUNT_NAME 'acct')",
            DuckDbAzureSecret.CreateStatement(Cred(CloudAuthMode.ServicePrincipal, tenant: "tid", client: "cid", secret: "shh")));

    [Fact]
    public void ServicePrincipal_MissingMaterial_Throws()
        => Assert.Throws<SqlFlowException>(() => DuckDbAzureSecret.CreateStatement(Cred(CloudAuthMode.ServicePrincipal, tenant: "tid")));

    [Fact]
    public void Statement_EscapesSingleQuotes()
    {
        var sql = DuckDbAzureSecret.CreateStatement(Cred(CloudAuthMode.ServicePrincipal, account: "ac'ct", tenant: "t", client: "c", secret: "p'q"));
        Assert.Contains("ACCOUNT_NAME 'ac''ct'", sql, StringComparison.Ordinal);
        Assert.Contains("CLIENT_SECRET 'p''q'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void StatementsFor_GeneratesWhenAzureLocationAndProviderResolves()
    {
        var statements = DuckDbAzureSecret.StatementsFor(
            Source("abfss://d@acct.dfs.core.windows.net/t"),
            new FakeProvider(Cred(CloudAuthMode.DefaultChain)));
        var only = Assert.Single(statements);
        Assert.Contains("CREATE OR REPLACE SECRET sqlflow_azure", only, StringComparison.Ordinal);
    }

    [Fact]
    public void StatementsFor_EmptyWhenNoProvider()
        => Assert.Empty(DuckDbAzureSecret.StatementsFor(Source("abfss://d@acct.dfs.core.windows.net/t"), provider: null));

    [Fact]
    public void StatementsFor_EmptyWhenProviderReturnsNull()
        => Assert.Empty(DuckDbAzureSecret.StatementsFor(Source("s3://bucket/x.parquet"), new FakeProvider(null)));

    [Fact]
    public void StatementsFor_EmptyWhenNoLocation()
        => Assert.Empty(DuckDbAzureSecret.StatementsFor(Source(null), new FakeProvider(Cred(CloudAuthMode.DefaultChain))));

    [Theory]
    [InlineData("off")]
    [InlineData("false")]
    [InlineData("none")]
    public void StatementsFor_EmptyWhenOptedOut(string value)
        => Assert.Empty(DuckDbAzureSecret.StatementsFor(
            Source("abfss://d@acct.dfs.core.windows.net/t", ("cloudAuth", value)),
            new FakeProvider(Cred(CloudAuthMode.DefaultChain))));

    [Theory]
    [InlineData("CREATE SECRET mine (TYPE AZURE, CONNECTION_STRING '...')")]
    [InlineData("create or replace secret mine (TYPE AZURE)")]
    [InlineData("CREATE PERSISTENT SECRET mine (TYPE AZURE)")]
    public void StatementsFor_EmptyWhenUserDeclaresSecretInInit(string init)
        => Assert.Empty(DuckDbAzureSecret.StatementsFor(
            Source("abfss://d@acct.dfs.core.windows.net/t", ("init", init)),
            new FakeProvider(Cred(CloudAuthMode.DefaultChain))));

    [Theory]
    [InlineData("SET azure_transport_option_type = 'curl'")]              // unrelated init
    [InlineData("SET memory_limit = '4GB' /* the secret sauce */")]       // the word 'secret' in a comment
    public void StatementsFor_GeneratesWhenInitMentionsSecretButDoesNotDeclareOne(string init)
        => Assert.Single(DuckDbAzureSecret.StatementsFor(
            Source("abfss://d@acct.dfs.core.windows.net/t", ("init", init)),
            new FakeProvider(Cred(CloudAuthMode.DefaultChain))));
}
