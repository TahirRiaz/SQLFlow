namespace SqlFlow.Core;

/// <summary>
/// A parsed Azure Storage location: the account, the container (filesystem), and the in-container blob path,
/// with enough of the original URI shape preserved to rebuild a per-blob URI in the caller's own scheme. It
/// accepts the two families SQLFlow users write:
/// <list type="bullet">
/// <item>The ADLS Gen2 / Databricks authority form: <c>abfss://container@account.dfs.core.windows.net/path</c>
/// (also <c>abfs</c>, and the legacy <c>wasbs</c>/<c>wasb</c> against the blob host).</item>
/// <item>The REST URL form: <c>https://account.blob.core.windows.net/container/path</c> (and the
/// <c>.dfs</c> host).</item>
/// </list>
/// The SDK always talks to the blob endpoint (<see cref="BlobServiceEndpoint"/>), which serves both flat and
/// hierarchical-namespace accounts, while <see cref="UriFor"/> reconstructs a listed blob's URI in the exact
/// scheme and host the caller used, so provenance columns and path masks see the location they were given.
/// <para>The type lives in SqlFlow.Core (no Azure SDK dependency) so every layer that only needs the parsed
/// coordinates - the storage engines, lineage identity, the catalog - shares one parser.</para>
/// </summary>
public sealed record AzureBlobLocation
{
    private const string BlobSuffix = ".blob.core.windows.net";
    private const string DfsSuffix = ".dfs.core.windows.net";

    /// <summary>The storage account name (the first host label).</summary>
    public required string Account { get; init; }

    /// <summary>The container (ADLS filesystem) name.</summary>
    public required string Container { get; init; }

    /// <summary>The in-container path with no leading slash; empty for the container root.</summary>
    public required string BlobPath { get; init; }

    /// <summary>The original URI scheme (e.g. <c>abfss</c>, <c>https</c>), lower-cased.</summary>
    public required string Scheme { get; init; }

    /// <summary>True when the container was carried as <c>container@host</c> (abfss/wasbs), false for <c>host/container</c> (https).</summary>
    public required bool ContainerInAuthority { get; init; }

    /// <summary>The host endpoint kind the caller used: <c>blob</c> or <c>dfs</c>. Preserved for round-tripping only.</summary>
    public required string EndpointKind { get; init; }

    /// <summary>The blob-service endpoint the SDK uses for this account (always the blob host).</summary>
    public Uri BlobServiceEndpoint => new($"https://{Account}{BlobSuffix}");

    /// <summary>Rebuilds a URI for <paramref name="inContainerPath"/> in the caller's original scheme and host shape.</summary>
    public string UriFor(string inContainerPath)
    {
        var path = inContainerPath.TrimStart('/');
        var host = $"{Account}.{EndpointKind}.core.windows.net";
        return ContainerInAuthority
            ? $"{Scheme}://{Container}@{host}/{path}"
            : $"{Scheme}://{host}/{Container}/{path}";
    }

    /// <summary>
    /// A scheme- and host-independent identity for an Azure Storage location, so the same container path
    /// written as <c>abfss://c@acct.dfs.core.windows.net/p</c> and read back as
    /// <c>https://acct.dfs.core.windows.net/c/p/</c> (or with a trailing slash) resolves to one lineage node.
    /// The account and container are case-folded (both are case-insensitive in Azure); the blob path keeps its
    /// case (blob names are case-sensitive) and its trailing slash is already trimmed by the parser. Returns
    /// <c>null</c> for a location that is not an Azure Storage URI, so callers fall back to their own identity.
    /// </summary>
    public static string? CanonicalIdentity(string? location)
    {
        if (!IsAzureStorageUri(location))
        {
            return null;
        }

        var parsed = Parse(location!);
        return $"az://{parsed.Account.ToLowerInvariant()}/{parsed.Container.ToLowerInvariant()}/{parsed.BlobPath}";
    }

    /// <summary>True if <paramref name="location"/> is an Azure Storage URI this store handles.</summary>
    public static bool IsAzureStorageUri(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return false;
        }

        var schemeEnd = location.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            return false;
        }

        var scheme = location[..schemeEnd].ToLowerInvariant();
        return scheme switch
        {
            // The authority form always names an Azure storage host after the '@'.
            "abfss" or "abfs" or "wasbs" or "wasb" => true,
            // A generic URL is Azure only when its host is a storage endpoint.
            "http" or "https" => HostOf(location[(schemeEnd + 3)..]) is { } host
                && (host.EndsWith(BlobSuffix, StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith(DfsSuffix, StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };
    }

    /// <summary>
    /// Parses an Azure Storage URI into its account, container, and blob path. Throws <see cref="SqlFlowException"/>
    /// with an actionable message when the URI is not a form this store can address (a bare <c>az://container</c>
    /// with no account, a non-Azure host, or a missing container).
    /// </summary>
    public static AzureBlobLocation Parse(string location)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);

        var schemeEnd = location.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            throw Invalid(location);
        }

        var scheme = location[..schemeEnd].ToLowerInvariant();
        var rest = location[(schemeEnd + 3)..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var authority = slash < 0 ? rest : rest[..slash];
        var afterAuthority = slash < 0 ? string.Empty : rest[(slash + 1)..];

        switch (scheme)
        {
            case "abfss":
            case "abfs":
            case "wasbs":
            case "wasb":
            {
                var at = authority.IndexOf('@', StringComparison.Ordinal);
                if (at <= 0)
                {
                    throw Invalid(location);
                }

                var container = authority[..at];
                var host = StripPort(authority[(at + 1)..]);
                var (account, endpointKind) = SplitHost(host, location);
                return new AzureBlobLocation
                {
                    Account = account,
                    Container = container,
                    BlobPath = TrimPath(afterAuthority),
                    Scheme = scheme,
                    ContainerInAuthority = true,
                    EndpointKind = endpointKind,
                };
            }

            case "http":
            case "https":
            {
                var host = StripPort(authority);
                var (account, endpointKind) = SplitHost(host, location);
                var pathSlash = afterAuthority.IndexOf('/', StringComparison.Ordinal);
                var container = pathSlash < 0 ? afterAuthority : afterAuthority[..pathSlash];
                if (container.Length == 0)
                {
                    throw new SqlFlowException(
                        $"Azure Storage URL '{location}' is missing a container. Use "
                        + "'https://<account>.blob.core.windows.net/<container>/<path>'.");
                }

                var blobPath = pathSlash < 0 ? string.Empty : afterAuthority[(pathSlash + 1)..];
                return new AzureBlobLocation
                {
                    Account = account,
                    Container = container,
                    BlobPath = TrimPath(blobPath),
                    Scheme = scheme,
                    ContainerInAuthority = false,
                    EndpointKind = endpointKind,
                };
            }

            default:
                throw Invalid(location);
        }
    }

    private static (string Account, string EndpointKind) SplitHost(string host, string location)
    {
        string suffix;
        string endpointKind;
        if (host.EndsWith(BlobSuffix, StringComparison.OrdinalIgnoreCase))
        {
            suffix = BlobSuffix;
            endpointKind = "blob";
        }
        else if (host.EndsWith(DfsSuffix, StringComparison.OrdinalIgnoreCase))
        {
            suffix = DfsSuffix;
            endpointKind = "dfs";
        }
        else
        {
            throw new SqlFlowException(
                $"'{location}' is not an Azure Storage location (host must end with '{BlobSuffix}' or '{DfsSuffix}').");
        }

        var account = host[..^suffix.Length];
        if (account.Length == 0 || !account.All(char.IsAsciiLetterOrDigit))
        {
            throw new SqlFlowException($"Could not read a storage account name from '{location}'.");
        }

        return (account, endpointKind);
    }

    private static string? HostOf(string authorityAndPath)
    {
        var slash = authorityAndPath.IndexOf('/', StringComparison.Ordinal);
        var authority = slash < 0 ? authorityAndPath : authorityAndPath[..slash];
        var at = authority.IndexOf('@', StringComparison.Ordinal);
        var host = at < 0 ? authority : authority[(at + 1)..];
        host = StripPort(host);
        return host.Length == 0 ? null : host;
    }

    private static string StripPort(string host)
    {
        var colon = host.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? host : host[..colon];
    }

    private static string TrimPath(string path)
    {
        var trimmed = path.Trim('/');
        // A query or fragment is not part of a stored path; drop it so the blob name is clean.
        var cut = trimmed.IndexOfAny(['?', '#']);
        return cut < 0 ? trimmed : trimmed[..cut];
    }

    private static SqlFlowException Invalid(string location) => new(
        $"'{location}' is not a supported Azure Storage location. Use 'abfss://<container>@<account>.dfs.core.windows.net/<path>' "
        + "or 'https://<account>.blob.core.windows.net/<container>/<path>'.");
}
