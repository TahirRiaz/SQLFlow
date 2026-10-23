using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core;

namespace SqlFlow.Azure.Storage;

/// <summary>
/// Access to Azure Storage over whichever of an account's two endpoints this host can reach. Every part of SQLFlow that
/// reads or writes Azure Storage (file discovery and loads, landing, exports, copies, SFTP transfers) takes its containers
/// from here, so they share one decision per account and one set of clients: the account is reached over its Blob
/// endpoint or its Data Lake (dfs) endpoint, whichever answers, as <see cref="AzureStorageEndpointSelector"/> decides,
/// whatever host the flow's location names. A location keeps its spelling everywhere it is shown; only the endpoint the
/// requests go to is chosen. <c>SQLFLOW_AZURE_STORAGE_ENDPOINT</c> (<c>auto</c>, <c>blob</c> or <c>dfs</c>) can fix
/// the choice for the process.
/// <para>Clients are built on first use and kept for the process: the Blob SDK's per account and credential, and the
/// Data Lake client's per account, container and credential. A container handle is cheap, and creating one neither
/// authenticates nor sends anything.</para>
/// </summary>
public sealed class AzureStorageAccess
{
    /// <summary>The setting that fixes the endpoint: <c>auto</c> (the default), <c>blob</c> or <c>dfs</c>.</summary>
    public const string EndpointVariable = "SQLFLOW_AZURE_STORAGE_ENDPOINT";

    private readonly IAzureCredentialFactory _credentials;
    private readonly AzureStorageAccessOptions _options;
    private readonly ConcurrentDictionary<ClientKey, Lazy<BlobServiceClient>> _blobServices = new();
    private readonly ConcurrentDictionary<ClientKey, Lazy<DfsRestClient>> _dfsClients = new();
    private readonly ConcurrentDictionary<TokenCredential, BearerTokenAuthenticationPolicy> _bearers = new(ReferenceEqualityComparer.Instance);

    public AzureStorageAccess(IAzureCredentialFactory credentials, ILogger<AzureStorageAccess>? logger = null)
        : this(credentials, AzureStorageAccessOptions.FromEnvironment(), logger)
    {
    }

    internal AzureStorageAccess(IAzureCredentialFactory credentials, AzureStorageAccessOptions options, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(options);
        _credentials = credentials;
        _options = options;
        Logger = logger ?? NullLogger.Instance;
        Selector = new AzureStorageEndpointSelector(
            AzureStorageEndpointSetting.Parse(options.EndpointSetting), options.ResolveHost, options.DnsTimeout, Logger);
    }

    internal AzureStorageEndpointSelector Selector { get; }

    internal ILogger Logger { get; }

    /// <summary>A container the location names, authenticated with the process credential.</summary>
    public AzureStorageContainer Container(AzureBlobLocation location) => Container(location, AzureStorageAuth.Ambient);

    /// <summary>A container the location names, authenticated as <paramref name="auth"/> says.</summary>
    public AzureStorageContainer Container(AzureBlobLocation location, AzureStorageAuth auth)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(auth);
        return new AzureStorageContainer(this, location, auth);
    }

    /// <summary>
    /// Builds a container's backends: its Blob endpoint always, its Data Lake endpoint when the credential can reach one
    /// (a connection string for a custom endpoint, or with neither a key nor a signature, cannot). Called on a container's
    /// first request, so a credential that cannot be built fails that request, not the handle's creation.
    /// </summary>
    internal ContainerBackends Connect(AzureBlobLocation location, AzureStorageAuth auth)
    {
        var account = location.Account;
        var container = location.Container;
        TokenCredential? credential = auth.Kind == AzureStorageAuthKind.Ambient ? _credentials.Create() : null;

        var blobService = BlobService(location, auth, credential, probe: false);
        var blobProbe = BlobService(location, auth, credential, probe: true);
        var blob = new BlobStorageBackend(blobService.GetBlobContainerClient(container), blobProbe.GetBlobContainerClient(container));

        var blobHost = blobService.Uri.Host;
        var (dfsEndpoint, dfsAuth) = DataLakeRoute(location, auth, credential, blobService);
        DfsStorageBackend? dfs = null;
        if (dfsEndpoint is not null && dfsAuth is not null)
        {
            dfs = new DfsStorageBackend(
                DfsClient(dfsEndpoint, container, auth, credential, dfsAuth, probe: false),
                DfsClient(dfsEndpoint, container, auth, credential, dfsAuth, probe: true),
                container);
        }

        return new ContainerBackends(account, blobHost, dfsEndpoint?.Host, blob, dfs);
    }

    private BlobServiceClient BlobService(AzureBlobLocation location, AzureStorageAuth auth, TokenCredential? credential, bool probe)
    {
        var key = new ClientKey("blob", auth.CacheKey, location.Account.ToLowerInvariant(), string.Empty, probe, credential);
        return _blobServices.GetOrAdd(key, _ => new Lazy<BlobServiceClient>(() =>
        {
            var options = BlobOptions(probe);
            return auth.Kind switch
            {
                AzureStorageAuthKind.Ambient => new BlobServiceClient(location.BlobServiceEndpoint, credential!, options),
                AzureStorageAuthKind.ConnectionString => new BlobServiceClient(auth.Secret!, options),
                AzureStorageAuthKind.AccountKey => new BlobServiceClient(
                    $"DefaultEndpointsProtocol=https;AccountName={location.Account};AccountKey={auth.Secret};EndpointSuffix=core.windows.net",
                    options),
                AzureStorageAuthKind.SharedAccessSignature => new BlobServiceClient(new Uri($"{location.BlobServiceEndpoint}?{auth.Secret}"), options),
                _ => throw new InvalidOperationException($"Unknown Azure Storage auth '{auth.Kind}'."),
            };
        })).Value;
    }

    private BlobClientOptions BlobOptions(bool probe)
    {
        var options = new BlobClientOptions();
        if (_options.Transport is not null)
        {
            options.Transport = _options.Transport;
        }

        if (probe)
        {
            options.Retry.MaxRetries = 0;
            options.Retry.NetworkTimeout = _options.ProbeTimeout;
        }
        else if (_options.RetryDelay is { } delay)
        {
            options.Retry.Delay = delay;
        }

        return options;
    }

    /// <summary>
    /// The Data Lake endpoint and how a request authenticates there, or nulls when the credential reaches none. A token
    /// and a signature are used as they are; a key (an account key, or a connection string's) signs an account signature.
    /// A connection string's endpoint is the Data Lake twin of its Blob endpoint, which only a standard Azure host has.
    /// </summary>
    private (Uri? Endpoint, Func<DfsPolicies>? Auth) DataLakeRoute(
        AzureBlobLocation location, AzureStorageAuth auth, TokenCredential? credential, BlobServiceClient blobService)
    {
        switch (auth.Kind)
        {
            case AzureStorageAuthKind.Ambient:
                return (location.DfsServiceEndpoint, () => new DfsPolicies([], [Bearer(credential!)]));
            case AzureStorageAuthKind.SharedAccessSignature:
                return (location.DfsServiceEndpoint, () => new DfsPolicies([new DfsSignaturePolicy(DfsSignature.Fixed(auth.Secret!))], []));
            case AzureStorageAuthKind.AccountKey:
                return (location.DfsServiceEndpoint, () => new DfsPolicies(
                    [new DfsSignaturePolicy(DfsSignature.SignedWith(blobService, _options.Time))], []));
            case AzureStorageAuthKind.ConnectionString:
            {
                var host = blobService.Uri.Host;
                const string blobSuffix = ".blob.core.windows.net";
                if (!host.EndsWith(blobSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    return (null, null);
                }

                var endpoint = new Uri($"https://{host[..^blobSuffix.Length]}.dfs.core.windows.net");
                if (StorageConnectionString.Setting(auth.Secret!, "SharedAccessSignature") is { } signature)
                {
                    return (endpoint, () => new DfsPolicies([new DfsSignaturePolicy(DfsSignature.Fixed(signature))], []));
                }

                if (blobService.CanGenerateAccountSasUri)
                {
                    return (endpoint, () => new DfsPolicies(
                        [new DfsSignaturePolicy(DfsSignature.SignedWith(blobService, _options.Time))], []));
                }

                return (null, null);
            }

            default:
                throw new InvalidOperationException($"Unknown Azure Storage auth '{auth.Kind}'.");
        }
    }

    private DfsRestClient DfsClient(
        Uri endpoint, string container, AzureStorageAuth auth, TokenCredential? credential, Func<DfsPolicies> policies, bool probe)
    {
        var key = new ClientKey("dfs", auth.CacheKey, endpoint.Host.ToLowerInvariant(), container, probe, credential);
        return _dfsClients.GetOrAdd(key, _ => new Lazy<DfsRestClient>(() =>
        {
            var (perCall, perRetry) = policies();
            var pipeline = DfsRestClient.CreatePipeline(
                perCall,
                perRetry,
                _options.Transport,
                maxRetries: probe ? 0 : 5,
                networkTimeout: probe ? _options.ProbeTimeout : null,
                retryDelay: probe ? null : _options.RetryDelay);
            return new DfsRestClient(endpoint, pipeline);
        })).Value;
    }

    // One bearer policy per credential, shared by the Data Lake clients that use it, so they share its token cache.
    private BearerTokenAuthenticationPolicy Bearer(TokenCredential credential)
        => _bearers.GetOrAdd(credential, static c => new BearerTokenAuthenticationPolicy(c, DfsRestClient.StorageScope));

    private sealed record DfsPolicies(HttpPipelinePolicy[] PerCall, HttpPipelinePolicy[] PerRetry);

    /// <summary>A client's identity: endpoint kind, auth (its kind and a hash of its secret), host, container, whether
    /// it is the probe's, and the process credential it was built with, compared by reference so a credential the factory
    /// rebuilt for a changed configuration gets clients of its own.</summary>
    private readonly struct ClientKey : IEquatable<ClientKey>
    {
        private readonly string _kind;
        private readonly string _auth;
        private readonly string _host;
        private readonly string _container;
        private readonly bool _probe;
        private readonly TokenCredential? _credential;

        public ClientKey(string kind, string auth, string host, string container, bool probe, TokenCredential? credential)
        {
            _kind = kind;
            _auth = auth;
            _host = host;
            _container = container;
            _probe = probe;
            _credential = credential;
        }

        public bool Equals(ClientKey other)
            => string.Equals(_kind, other._kind, StringComparison.Ordinal)
               && string.Equals(_auth, other._auth, StringComparison.Ordinal)
               && string.Equals(_host, other._host, StringComparison.Ordinal)
               && string.Equals(_container, other._container, StringComparison.Ordinal)
               && _probe == other._probe
               && ReferenceEquals(_credential, other._credential);

        public override bool Equals(object? obj) => obj is ClientKey other && Equals(other);

        public override int GetHashCode()
            => HashCode.Combine(_kind, _auth, _host, _container, _probe, _credential is null ? 0 : RuntimeHelpers.GetHashCode(_credential));
    }
}

/// <summary>The settings of <see cref="AzureStorageAccess"/>; tests replace the transport, the resolver and the clock.</summary>
internal sealed record AzureStorageAccessOptions
{
    /// <summary>The raw value of <see cref="AzureStorageAccess.EndpointVariable"/>.</summary>
    public string? EndpointSetting { get; init; }

    /// <summary>The HTTP transport every client uses; the Azure SDK's default when null.</summary>
    public HttpPipelineTransport? Transport { get; init; }

    /// <summary>Resolves a host name to its addresses, for the private-endpoint hint.</summary>
    public Func<string, CancellationToken, Task<IPAddress[]>> ResolveHost { get; init; } = Dns.GetHostAddressesAsync;

    /// <summary>How long a probe waits for an endpoint's answer.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long the resolution of one host name may take before it counts as not resolving.</summary>
    public TimeSpan DnsTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>The clock a key-signed signature's lifetime is measured on.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>The first retry's delay for the clients' requests; the Azure SDK's default (0.8 s, doubling) when null.</summary>
    public TimeSpan? RetryDelay { get; init; }

    public static AzureStorageAccessOptions FromEnvironment()
        => new() { EndpointSetting = Environment.GetEnvironmentVariable(AzureStorageAccess.EndpointVariable) };
}

/// <summary>One container's backends, and the account identity the selector decides for.</summary>
internal sealed class ContainerBackends
{
    internal ContainerBackends(string account, string blobHost, string? dfsHost, IAzureStorageBackend blob, IAzureStorageBackend? dfs)
    {
        Blob = blob;
        Dfs = dfs;
        Target = new EndpointTarget(account, blobHost, dfs is null ? null : dfsHost, (endpoint, ct) => Backend(endpoint).ProbeAsync(ct));
    }

    internal IAzureStorageBackend Blob { get; }

    internal IAzureStorageBackend? Dfs { get; }

    internal EndpointTarget Target { get; }

    internal IAzureStorageBackend Backend(AzureStorageEndpoint endpoint)
        => endpoint == AzureStorageEndpoint.Dfs
            ? Dfs ?? throw new SqlFlowException($"Azure Storage account '{Target.Account}' has no Data Lake endpoint for this credential.")
            : Blob;
}
