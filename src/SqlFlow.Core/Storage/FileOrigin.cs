namespace SqlFlow.Core;

/// <summary>What kind of system a file endpoint originates from, so the catalog can group it under its
/// provider (the top level of the file tree) and icon it. Determined from the identity's scheme, so it is
/// authoritative, not guessed.</summary>
public enum FileOriginKind
{
    /// <summary>An Azure Storage account (the canonical <c>az://account/container/path</c> identity, or any
    /// abfss/wasbs/blob/dfs spelling that folds to it).</summary>
    AzureStorage,

    /// <summary>An Amazon S3 bucket (<c>s3://bucket/path</c>).</summary>
    AmazonS3,

    /// <summary>A Google Cloud Storage bucket (<c>gs://bucket/path</c>).</summary>
    GoogleCloud,

    /// <summary>An SFTP/FTP server (<c>sftp://host:port/path</c>).</summary>
    Sftp,

    /// <summary>A network file share (a UNC <c>\\server\share</c> path): the server is the origin.</summary>
    NetworkShare,

    /// <summary>A path on the local/estate filesystem (a relative or rooted path, no URI scheme).</summary>
    Local,

    /// <summary>Any other URI scheme: the host is the origin.</summary>
    Other,
}

/// <summary>
/// The canonical PARENT of a file endpoint, parsed from its lineage identity, so a file groups under the
/// system it lives in exactly as a table groups under its database. A lake file's parent is its storage
/// account (then the container, then the folder path); an SFTP file's parent is its server; a UNC file's is
/// its share host; a local file's is the filesystem. This is a pure function of a file's canonical identity,
/// so nothing new need be authored in a flow document: the origin is derived, never stored.
/// <para>Every SQLFlow flow type that names a file (file ingestion sources, export/copy/acquire targets, SFTP
/// remote and local paths, invoke outputs) funnels through the same identity normalization, so the four shapes
/// handled here (Azure in any spelling, <c>sftp://</c>, any other <c>scheme://</c>, and a filesystem/UNC path)
/// cover them all. The parser NEVER throws and NEVER returns empty fields: a malformed or unrecognizable
/// identity yields a single <see cref="UnknownOrigin"/> leaf so the node is shown, not dropped.</para>
/// </summary>
public sealed record FileOrigin
{
    public required FileOriginKind Kind { get; init; }

    /// <summary>The origin system: the storage account, the <c>host:port</c> of an SFTP server, a UNC/generic
    /// URI host, or <see cref="LocalOrigin"/> for a filesystem path. The top level of the file tree; never
    /// empty.</summary>
    public required string Origin { get; init; }

    /// <summary>The second level under the origin when the origin has one: the Azure container (ADLS
    /// filesystem) or a UNC share. Null for an SFTP, local, or generic origin, which have no container concept.</summary>
    public string? Container { get; init; }

    /// <summary>The folders between the container/origin and the leaf, slash-joined with no leading or trailing
    /// slash; null when the leaf sits directly under its origin/container.</summary>
    public string? Path { get; init; }

    /// <summary>The leaf node's display name: the last path segment (a file, or the folder a flow reads); never
    /// empty.</summary>
    public required string Name { get; init; }

    /// <summary>The origin label for a filesystem path (a checkout-relative or rooted local location).</summary>
    public const string LocalOrigin = "Local files";

    /// <summary>The origin/leaf label used when an identity cannot be decomposed (empty or malformed).</summary>
    public const string UnknownOrigin = "(unknown)";

    /// <summary>
    /// Decomposes a file endpoint's canonical identity into its origin, container, folder path, and leaf name.
    /// Never throws.
    /// </summary>
    public static FileOrigin Parse(string identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var value = identity.Trim();
        if (value.Length == 0)
        {
            return new FileOrigin { Kind = FileOriginKind.Local, Origin = UnknownOrigin, Name = UnknownOrigin };
        }

        // Fold any Azure spelling (raw abfss/wasbs, or an https/dfs storage URL) to the canonical az:// identity
        // first, so the same account groups as one origin however a flow wrote the URI. Guarded: a URI that only
        // looks Azure (e.g. abfss with no container) throws inside the parser, and falls through to generic
        // handling rather than propagating.
        try
        {
            if (AzureBlobLocation.CanonicalIdentity(value) is { Length: > 0 } canonical)
            {
                value = canonical;
            }
        }
        catch (SqlFlowException)
        {
            // Not a well-formed Azure URI after all; parse it as a generic scheme or local path below.
        }

        if (value.StartsWith("az://", StringComparison.OrdinalIgnoreCase))
        {
            return ParseAzure(value);
        }

        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd > 0 && IsValidScheme(value.AsSpan(0, schemeEnd)))
        {
            return ParseScheme(value, schemeEnd);
        }

        return ParseLocalOrUnc(value);
    }

    /// <summary>The canonical <c>az://account/container/blobpath</c> form: account is the origin, container the
    /// second level, the remaining segments the folders and leaf. Account and container are already case-folded
    /// by <see cref="AzureBlobLocation.CanonicalIdentity"/>.</summary>
    private static FileOrigin ParseAzure(string value)
    {
        var parts = CleanSegments(StripQuery(value[5..]));
        var account = parts.Length > 0 ? parts[0] : UnknownOrigin;
        var hasContainer = parts.Length > 1;
        var container = hasContainer ? parts[1] : null;
        var rest = parts.Length > 2 ? parts[2..] : [];
        // With no blob path, the container itself is the leaf under the account (no empty container level).
        var (path, name) = rest.Length > 0 ? SplitLeaf(rest) : (null, container ?? account);
        return new FileOrigin
        {
            Kind = FileOriginKind.AzureStorage,
            Origin = account,
            Container = rest.Length > 0 ? container : null,
            Path = path,
            Name = name,
        };
    }

    /// <summary>A generic <c>scheme://host/path</c> identity: the host is the origin (case-folded, as hosts and
    /// SFTP servers are case-insensitive), the path the folders and leaf. SFTP/FTP schemes are typed as
    /// <see cref="FileOriginKind.Sftp"/>; anything else as <see cref="FileOriginKind.Other"/>.</summary>
    private static FileOrigin ParseScheme(string value, int schemeEnd)
    {
        var scheme = value[..schemeEnd].ToLowerInvariant();
        var rest = value[(schemeEnd + 3)..];
        var firstSlash = rest.IndexOf('/', StringComparison.Ordinal);
        var authority = firstSlash < 0 ? rest : rest[..firstSlash];
        // The SFTP collector writes 'sftp://host:port' + a RemotePath that can be a bare '.', leaving a trailing
        // dot on the authority; trim it so the server groups cleanly. A userinfo prefix (user@host) is dropped:
        // the credential is not identity.
        var host = StripQuery(authority).TrimEnd('.');
        var at = host.LastIndexOf('@');
        if (at >= 0 && at < host.Length - 1)
        {
            host = host[(at + 1)..];
        }

        host = host.ToLowerInvariant();
        var segments = CleanSegments(StripQuery(firstSlash < 0 ? string.Empty : rest[(firstSlash + 1)..]));
        var (path, name) = segments.Length > 0 ? SplitLeaf(segments) : (null, host.Length > 0 ? host : UnknownOrigin);
        return new FileOrigin
        {
            Kind = scheme switch
            {
                "sftp" or "ftp" or "ftps" => FileOriginKind.Sftp,
                "s3" => FileOriginKind.AmazonS3,
                "gs" or "gcs" => FileOriginKind.GoogleCloud,
                _ => FileOriginKind.Other,
            },
            Origin = host.Length > 0 ? host : UnknownOrigin,
            Path = path,
            Name = name,
        };
    }

    /// <summary>A filesystem identity with no URI scheme. A UNC path (<c>//server/share/…</c>, from a Windows
    /// <c>\\server\share</c> location) is a network origin: the server is the origin and the share its container.
    /// A relative or rooted local path groups under <see cref="LocalOrigin"/>.</summary>
    private static FileOrigin ParseLocalOrUnc(string value)
    {
        var normalized = StripQuery(value.Replace('\\', '/'));
        if (normalized.StartsWith("//", StringComparison.Ordinal))
        {
            var uncParts = CleanSegments(normalized);
            var server = uncParts.Length > 0 ? uncParts[0].ToLowerInvariant() : UnknownOrigin;
            var hasShare = uncParts.Length > 1;
            var rest = uncParts.Length > 2 ? uncParts[2..] : [];
            var (uncPath, uncName) = rest.Length > 0
                ? SplitLeaf(rest)
                : (null, hasShare ? uncParts[1] : server);
            return new FileOrigin
            {
                Kind = FileOriginKind.NetworkShare,
                Origin = server,
                Container = rest.Length > 0 ? uncParts[1] : null,
                Path = uncPath,
                Name = uncName,
            };
        }

        var segments = CleanSegments(normalized);
        var (path, name) = segments.Length > 0 ? SplitLeaf(segments) : (null, UnknownOrigin);
        return new FileOrigin
        {
            Kind = FileOriginKind.Local,
            Origin = LocalOrigin,
            Path = path,
            Name = name,
        };
    }

    /// <summary>Splits folder segments into the parent path (all but the last, slash-joined) and the leaf name
    /// (the last). Callers guarantee at least one segment.</summary>
    private static (string? Path, string Name) SplitLeaf(string[] segments)
        => segments.Length == 1 ? (null, segments[0]) : (string.Join('/', segments[..^1]), segments[^1]);

    /// <summary>Splits a slash path into non-empty segments, dropping empty runs (<c>a//b</c>) and no-op
    /// <c>.</c> segments so the folder tree is clean.</summary>
    private static string[] CleanSegments(string path)
        => path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(segment => segment != ".")
            .ToArray();

    /// <summary>Trims a query string or fragment (<c>?…</c> / <c>#…</c>, e.g. a SAS token) off a path portion so
    /// it never leaks into a folder or file name.</summary>
    private static string StripQuery(string value)
    {
        var cut = value.AsSpan().IndexOfAny('?', '#');
        return cut < 0 ? value : value[..cut];
    }

    /// <summary>Whether the text before a <c>://</c> is a real URI scheme (a letter, then letters/digits/
    /// <c>+</c>/<c>-</c>/<c>.</c>, at least two characters), so a Windows drive letter (<c>C:</c>) or other
    /// stray colon is never mistaken for a scheme.</summary>
    private static bool IsValidScheme(ReadOnlySpan<char> scheme)
    {
        if (scheme.Length < 2 || !char.IsAsciiLetter(scheme[0]))
        {
            return false;
        }

        foreach (var c in scheme)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }
}
