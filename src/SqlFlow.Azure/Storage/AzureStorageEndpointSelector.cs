using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Azure;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;

namespace SqlFlow.Azure.Storage;

/// <summary>
/// Decides, once per storage account and process, which endpoint the account is reached on, and remembers it.
/// <list type="number">
/// <item>An explicit choice wins: <c>SQLFLOW_AZURE_STORAGE_ENDPOINT</c> set to <c>blob</c> or <c>dfs</c> uses that endpoint
/// and nothing else. An account whose credentials name no Data Lake endpoint (a connection string for a custom or emulated
/// endpoint) has only its Blob endpoint.</item>
/// <item>Otherwise both host names are resolved, and one that resolves only to private addresses is tried first: that is a
/// private endpoint wired into this host's network, which is the route the network was built for. On a tie the Blob
/// endpoint goes first, as it always has.</item>
/// <item>Each candidate is probed in turn with one request to the container that is never retried and gives up after the
/// probe timeout. Any answer the account gives (a 404, or a permission error the real request will report) proves the
/// endpoint can be used. A network rejection (403 <c>AuthorizationFailure</c>), a name that does not resolve, a refused
/// or timed-out connection prove it cannot, and the next candidate is probed.</item>
/// </list>
/// A failed detection is not remembered, so the next request detects again (the network may have been fixed). A credential
/// failure is not an endpoint's fault: it surfaces as itself, and nothing is remembered.
/// </summary>
internal sealed class AzureStorageEndpointSelector
{
    private static readonly IPNetwork[] PrivateRanges =
    [
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("fc00::/7"),
    ];

    private readonly ConcurrentDictionary<string, Lazy<Task<AzureStorageEndpoint>>> _chosen = new(StringComparer.Ordinal);
    private readonly AzureStorageEndpointSetting _setting;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly TimeSpan _dnsTimeout;
    private readonly ILogger _logger;

    internal AzureStorageEndpointSelector(
        AzureStorageEndpointSetting setting, Func<string, CancellationToken, Task<IPAddress[]>> resolve, TimeSpan dnsTimeout, ILogger logger)
    {
        _setting = setting;
        _resolve = resolve;
        _dnsTimeout = dnsTimeout;
        _logger = logger;
    }

    /// <summary>True when the endpoint is chosen by detection, so a request refused on one may move to the other.</summary>
    internal bool Detects(EndpointTarget target) => _setting.Forced is null && _setting.Error is null && target.DfsHost is not null;

    internal async Task<AzureStorageEndpoint> SelectAsync(EndpointTarget target, CancellationToken ct)
    {
        if (_setting.Error is { } error)
        {
            throw new SqlFlowException(error);
        }

        if (_setting.Forced is { } forced)
        {
            if (forced == AzureStorageEndpoint.Dfs && target.DfsHost is null)
            {
                throw new SqlFlowException(
                    $"{AzureStorageAccess.EndpointVariable} is 'dfs', but the storage endpoint '{target.BlobHost}' has no Data Lake "
                    + "endpoint to use: its connection string names a custom Blob endpoint, or carries neither a key nor a shared access signature.");
            }

            return forced;
        }

        if (target.DfsHost is null)
        {
            return AzureStorageEndpoint.Blob;
        }

        var lazy = _chosen.GetOrAdd(target.Key, _ => new Lazy<Task<AzureStorageEndpoint>>(() => DetectAsync(target)));
        try
        {
            return await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        catch when (lazy.Value.IsFaulted || lazy.Value.IsCanceled)
        {
            // Not remembered: the next request detects again.
            _chosen.TryRemove(new KeyValuePair<string, Lazy<Task<AzureStorageEndpoint>>>(target.Key, lazy));
            throw;
        }
    }

    /// <summary>Forgets the account's endpoint when it is <paramref name="failed"/>, so the next request detects again.</summary>
    internal void Forget(EndpointTarget target, AzureStorageEndpoint failed)
    {
        if (_chosen.TryGetValue(target.Key, out var lazy)
            && lazy.IsValueCreated
            && lazy.Value.IsCompletedSuccessfully
            && lazy.Value.Result == failed)
        {
            _chosen.TryRemove(new KeyValuePair<string, Lazy<Task<AzureStorageEndpoint>>>(target.Key, lazy));
        }
    }

    private async Task<AzureStorageEndpoint> DetectAsync(EndpointTarget target)
    {
        var candidates = await OrderAsync(target).ConfigureAwait(false);
        var refused = new List<(Candidate Candidate, Exception Failure)>();
        foreach (var candidate in candidates)
        {
            try
            {
                await target.ProbeAsync(candidate.Endpoint, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (Unusable(ex))
            {
                refused.Add((candidate, ex));
                continue;
            }
            catch (RequestFailedException)
            {
                // The account answered (a missing container, a permission the real request will report): reachable.
            }

            LogChoice(target, candidate, refused);
            return candidate.Endpoint;
        }

        throw Unreachable(target, refused);
    }

    /// <summary>True when a probe's failure shows the endpoint cannot be used from here at all.</summary>
    private static bool Unusable(Exception exception)
        => AzureStorageFailures.Leaves(exception).All(leaf =>
            AzureStorageFailures.IsNetworkRejection(leaf)
            || AzureStorageFailures.IsConnectionFailure(leaf)
            || leaf is RequestFailedException { Status: 0 }
            || leaf is OperationCanceledException or HttpRequestException or IOException or SocketException);

    private async Task<IReadOnlyList<Candidate>> OrderAsync(EndpointTarget target)
    {
        var blob = ResolveAsync(target.BlobHost);
        var dfs = ResolveAsync(target.DfsHost!);
        await Task.WhenAll(blob, dfs).ConfigureAwait(false);
        Candidate[] candidates =
        [
            new(AzureStorageEndpoint.Blob, target.BlobHost, await blob.ConfigureAwait(false)),
            new(AzureStorageEndpoint.Dfs, target.DfsHost!, await dfs.ConfigureAwait(false)),
        ];

        // OrderBy is stable, so on a tie the Blob endpoint stays first.
        return candidates.OrderBy(candidate => candidate.Resolution.Rank).ToList();
    }

    private async Task<HostResolution> ResolveAsync(string host)
    {
        using var timeout = new CancellationTokenSource(_dnsTimeout);
        try
        {
            var addresses = await _resolve(host, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            return new HostResolution(addresses, Failure: addresses.Length == 0 ? "resolves to no address" : null);
        }
        catch (SocketException ex)
        {
            return new HostResolution([], $"does not resolve ({ex.SocketErrorCode})");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return new HostResolution([], $"did not resolve within {_dnsTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s");
        }
    }

    private void LogChoice(EndpointTarget target, Candidate chosen, List<(Candidate Candidate, Exception Failure)> refused)
    {
        if (refused.Count == 0)
        {
            _logger.LogInformation(
                "Azure Storage account {Account} is reached over its {Endpoint} endpoint ({Host}, {Resolution}).",
                target.Account, chosen.Endpoint.HostLabel(), chosen.Host, chosen.Resolution.Describe());
            return;
        }

        var (other, failure) = refused[0];
        _logger.LogInformation(
            "Azure Storage account {Account} is reached over its {Endpoint} endpoint ({Host}, {Resolution}): its {Other} endpoint "
            + "({OtherHost}, {OtherResolution}) is not reachable from this host: {Reason}.",
            target.Account, chosen.Endpoint.HostLabel(), chosen.Host, chosen.Resolution.Describe(),
            other.Endpoint.HostLabel(), other.Host, other.Resolution.Describe(), AzureStorageFailures.Describe(failure));
    }

    private static SqlFlowException Unreachable(EndpointTarget target, List<(Candidate Candidate, Exception Failure)> refused)
    {
        var details = string.Join("; ", refused.Select(entry =>
            $"{entry.Candidate.Endpoint.HostLabel()} ({entry.Candidate.Host}, {entry.Candidate.Resolution.Describe()}): "
            + AzureStorageFailures.Describe(entry.Failure)));
        // Detection probes both endpoints before it gets here, so both refusals are in hand; the first is the cause.
        return new SqlFlowException(
            $"Azure Storage account '{target.Account}' cannot be reached from this host over either of its endpoints: {details}. "
            + "It is reached once this host's network admits one of them: a private endpoint for the account's 'blob' or 'dfs' "
            + "sub-resource with its privatelink DNS zone linked to the network, or a storage firewall rule that admits this host.",
            refused[0].Failure);
    }

    private sealed record Candidate(AzureStorageEndpoint Endpoint, string Host, HostResolution Resolution);

    /// <summary>What a host name resolved to: private addresses rank first, public next, no resolution last.</summary>
    private sealed record HostResolution(IPAddress[] Addresses, string? Failure)
    {
        public int Rank => Addresses.Length == 0 ? 2 : Addresses.All(IsPrivate) ? 0 : 1;

        public string Describe()
        {
            if (Addresses.Length == 0)
            {
                return Failure ?? "does not resolve";
            }

            var kind = Rank == 0 ? "private" : "public";
            return $"resolves to {kind} {string.Join(", ", Addresses.Select(address => address.ToString()))}";
        }
    }

    internal static bool IsPrivate(IPAddress address)
    {
        var candidate = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return PrivateRanges.Any(range => range.Contains(candidate));
    }
}

/// <summary>The account a selector decides for: its two host names and how to probe each.</summary>
internal sealed class EndpointTarget
{
    private readonly Func<AzureStorageEndpoint, CancellationToken, Task> _probe;

    internal EndpointTarget(string account, string blobHost, string? dfsHost, Func<AzureStorageEndpoint, CancellationToken, Task> probe)
    {
        Account = account;
        BlobHost = blobHost.ToLowerInvariant();
        DfsHost = dfsHost?.ToLowerInvariant();
        _probe = probe;
        Key = $"{BlobHost}|{DfsHost}";
    }

    internal string Account { get; }

    internal string BlobHost { get; }

    internal string? DfsHost { get; }

    /// <summary>The account's identity for the remembered choice: its host names (the network admits hosts, not
    /// credentials, so every container and credential of the account shares one choice).</summary>
    internal string Key { get; }

    internal Task ProbeAsync(AzureStorageEndpoint endpoint, CancellationToken ct) => _probe(endpoint, ct);
}

/// <summary>The value of <c>SQLFLOW_AZURE_STORAGE_ENDPOINT</c>: detection (unset or <c>auto</c>), one endpoint, or a
/// value it does not take, which every Azure Storage request then reports.</summary>
internal sealed record AzureStorageEndpointSetting(AzureStorageEndpoint? Forced, string? Error)
{
    internal static AzureStorageEndpointSetting Parse(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || string.Equals(text, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return new AzureStorageEndpointSetting(null, null);
        }

        if (string.Equals(text, "blob", StringComparison.OrdinalIgnoreCase))
        {
            return new AzureStorageEndpointSetting(AzureStorageEndpoint.Blob, null);
        }

        if (string.Equals(text, "dfs", StringComparison.OrdinalIgnoreCase))
        {
            return new AzureStorageEndpointSetting(AzureStorageEndpoint.Dfs, null);
        }

        return new AzureStorageEndpointSetting(
            null,
            $"{AzureStorageAccess.EndpointVariable} is '{text}'; it takes 'auto' (the default: whichever endpoint this host "
            + "reaches), 'blob' or 'dfs'.");
    }
}
