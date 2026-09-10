using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Secrets;
using Xunit;

namespace SqlFlow.Tests;

public sealed class ConnectionResolverTests
{
    private const string PasswordlessTarget = "Server=tcp:dw;Initial Catalog=Silver;Authentication=Active Directory Default";

    [Fact]
    public async Task Inline_LiteralString_RequiresSelfAuthenticating_AndStampsTargetRole()
    {
        var canonicalizer = new RecordingCanonicalizer(DataSourceKind.MSSQL);
        var resolver = Build(StoreWithoutAliases(), canonicalizer);

        var resolved = await resolver.ResolveAsync(PasswordlessTarget, ConnectionRole.Target);

        Assert.Equal(DataSourceKind.MSSQL, resolved.Kind);
        Assert.Equal($"CANON({PasswordlessTarget})", resolved.CanonicalString);
        Assert.Equal("REDACTED", resolved.RedactedString);
        Assert.Equal(PasswordlessTarget, canonicalizer.LastConnectionString);
        Assert.Equal(ConnectionRole.Target, canonicalizer.LastRole);
        Assert.Equal(SecretlessPolicy.RequireSelfAuthenticating, canonicalizer.LastPolicy);
    }

    [Fact]
    public async Task Inline_WholeSecretReference_IsExpanded_AndTrusted()
    {
        const string realConnection = "Server=tcp:dw;Initial Catalog=Silver;User ID=u;Password=p";
        var canonicalizer = new RecordingCanonicalizer(DataSourceKind.MSSQL);
        var resolver = Build(StoreWithoutAliases(), canonicalizer, ("keyvault", new() { ["kv/dw"] = realConnection }));

        await resolver.ResolveAsync("${keyvault:kv/dw}", ConnectionRole.Target);

        Assert.Equal(realConnection, canonicalizer.LastConnectionString);   // expanded before canonicalization
        Assert.Equal(SecretlessPolicy.Trusted, canonicalizer.LastPolicy);   // whole-ref => trusted, gate skipped
    }

    [Fact]
    public async Task Inline_Source_StampsSourceRole()
    {
        var canonicalizer = new RecordingCanonicalizer(DataSourceKind.MSSQL);
        var resolver = Build(StoreWithoutAliases(), canonicalizer);

        await resolver.ResolveAsync(PasswordlessTarget, ConnectionRole.Source);

        Assert.Equal(ConnectionRole.Source, canonicalizer.LastRole);
    }

    [Fact]
    public async Task Alias_InLightweightMode_Throws_WithoutCallingStore()
    {
        var store = StoreWithoutAliases();
        var resolver = Build(store, new RecordingCanonicalizer());

        var ex = await Assert.ThrowsAsync<SqlFlowException>(
            () => resolver.ResolveAsync("@dwh", ConnectionRole.Target));

        Assert.Contains("full mode", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(store.Called);
    }

    [Fact]
    public async Task Alias_ResolvesDataSource_AndCarriesCapabilities()
    {
        var dataSource = new DataSource
        {
            Alias = "dwh",
            Kind = DataSourceKind.AZDB,
            ConnectionRef = PasswordlessTarget,
            Capabilities = new DataSourceCapabilities { IsSynapse = true, SupportsCrossDbRef = true },
            Credential = new CredentialProfile { Mode = CredentialMode.ConnectionStringAuth },
        };
        var canonicalizer = new RecordingCanonicalizer(DataSourceKind.AZDB);
        var resolver = Build(StoreWith(dataSource), canonicalizer);

        var resolved = await resolver.ResolveAsync("@dwh", ConnectionRole.Target);

        Assert.Equal(DataSourceKind.AZDB, resolved.Kind);
        Assert.True(resolved.Capabilities.IsSynapse);
        Assert.True(resolved.Capabilities.SupportsCrossDbRef);
        Assert.Equal(SecretlessPolicy.RequireSelfAuthenticating, canonicalizer.LastPolicy);
    }

    [Fact]
    public async Task Alias_InjectedTokenMode_UsesRejectRestingSecretPolicy()
    {
        var dataSource = Ds("partner", DataSourceKind.AZDB, CredentialMode.InjectedToken);
        var canonicalizer = new RecordingCanonicalizer(DataSourceKind.AZDB);
        var resolver = Build(StoreWith(dataSource), canonicalizer);

        await resolver.ResolveAsync("@partner", ConnectionRole.Target);

        Assert.Equal(SecretlessPolicy.RejectRestingSecret, canonicalizer.LastPolicy);
    }

    [Fact]
    public async Task Alias_InlineConnectionStringMode_UsesTrustedPolicy_AndExpands()
    {
        const string realConnection = "Server=tcp:legacy;Initial Catalog=Db;User ID=u;Password=p";
        var dataSource = new DataSource
        {
            Alias = "legacy",
            Kind = DataSourceKind.MSSQL,
            ConnectionRef = "${keyvault:kv/legacy}",
            Credential = new CredentialProfile { Mode = CredentialMode.InlineConnectionString },
        };
        var canonicalizer = new RecordingCanonicalizer(DataSourceKind.MSSQL);
        var resolver = Build(StoreWith(dataSource), canonicalizer, ("keyvault", new() { ["kv/legacy"] = realConnection }));

        await resolver.ResolveAsync("@legacy", ConnectionRole.Target);

        Assert.Equal(realConnection, canonicalizer.LastConnectionString);
        Assert.Equal(SecretlessPolicy.Trusted, canonicalizer.LastPolicy);
    }

    [Fact]
    public async Task NoCanonicalizerForKind_Throws()
    {
        var dataSource = Ds("mysql-src", DataSourceKind.MySQL, CredentialMode.ConnectionStringAuth);
        var resolver = Build(StoreWith(dataSource), new RecordingCanonicalizer(DataSourceKind.MSSQL)); // handles MSSQL only

        await Assert.ThrowsAsync<SqlFlowException>(() => resolver.ResolveAsync("@mysql-src", ConnectionRole.Source));
    }

    [Fact]
    public async Task Null_RawReference_Throws()
    {
        var resolver = Build(StoreWithoutAliases(), new RecordingCanonicalizer());
        await Assert.ThrowsAsync<ArgumentNullException>(() => resolver.ResolveAsync(null!, ConnectionRole.Target));
    }

    // --- helpers and fakes ---

    private static ConnectionResolver Build(
        FakeDataSourceStore store,
        IConnectionStringCanonicalizer canonicalizer,
        params (string Scheme, Dictionary<string, string> Values)[] secretProviders)
    {
        var providers = secretProviders.Length == 0
            ? new List<ISecretProvider> { new EnvSecretProvider() }
            : secretProviders.Select(p => (ISecretProvider)new FakeSecretProvider(p.Scheme, p.Values)).ToList();
        return new ConnectionResolver(store, new SecretResolver(providers), [canonicalizer]);
    }

    private static DataSource Ds(string alias, DataSourceKind kind, CredentialMode mode) => new()
    {
        Alias = alias,
        Kind = kind,
        ConnectionRef = PasswordlessTarget,
        Credential = new CredentialProfile { Mode = mode },
    };

    private static FakeDataSourceStore StoreWithoutAliases() => new(supportsAliases: false);

    private static FakeDataSourceStore StoreWith(DataSource dataSource)
        => new(supportsAliases: true, new Dictionary<string, DataSource> { [dataSource.Alias] = dataSource });

    private sealed class FakeDataSourceStore : IDataSourceStore
    {
        private readonly IReadOnlyDictionary<string, DataSource> _sources;

        public FakeDataSourceStore(bool supportsAliases, IReadOnlyDictionary<string, DataSource>? sources = null)
        {
            SupportsAliases = supportsAliases;
            _sources = sources ?? new Dictionary<string, DataSource>();
        }

        public bool SupportsAliases { get; }

        public bool Called { get; private set; }

        public Task<DataSource> ResolveAsync(string aliasName, CancellationToken ct = default)
        {
            Called = true;
            return _sources.TryGetValue(aliasName, out var dataSource)
                ? Task.FromResult(dataSource)
                : throw new SqlFlowException($"Unknown alias '{aliasName}'.");
        }
    }

    private sealed class FakeSecretProvider : ISecretProvider
    {
        private readonly IReadOnlyDictionary<string, string> _values;

        public FakeSecretProvider(string scheme, IReadOnlyDictionary<string, string> values)
        {
            Scheme = scheme;
            _values = values;
        }

        public string Scheme { get; }

        public string Resolve(string locator)
            => _values.TryGetValue(locator, out var value) ? value : throw new SqlFlowException($"Unknown secret '{locator}'.");
    }

    private sealed class RecordingCanonicalizer : IConnectionStringCanonicalizer
    {
        private readonly DataSourceKind _handles;

        public RecordingCanonicalizer(DataSourceKind handles = DataSourceKind.MSSQL) => _handles = handles;

        public string? LastConnectionString { get; private set; }

        public ConnectionRole? LastRole { get; private set; }

        public SecretlessPolicy? LastPolicy { get; private set; }

        public bool CanHandle(DataSourceKind kind) => kind == _handles;

        public CanonicalConnection Canonicalize(string connectionString, ConnectionRole role, SecretlessPolicy policy)
        {
            LastConnectionString = connectionString;
            LastRole = role;
            LastPolicy = policy;
            return new CanonicalConnection { Canonical = $"CANON({connectionString})", Redacted = "REDACTED" };
        }
    }
}
