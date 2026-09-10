using System.IO.Enumeration;
using System.Text.RegularExpressions;

namespace SqlFlow.Core.Files;

/// <summary>
/// A file-selection spec: what a file endpoint points at, as the engine's file readers see it. <see cref="Location"/>
/// is the folder (or a full file path) the endpoint reads from or writes to; <see cref="Glob"/> is the file-name
/// pattern (the source's <c>srcFile</c> option) and defaults per <see cref="Type"/> when absent; <see cref="Mask"/>
/// is an optional regex over the full path (<c>srcPathMask</c>); <see cref="SrcPath"/> is the alternate root the
/// file loader accepts in place of <see cref="Location"/>. The same shape describes a file ingestion's source and an
/// invoke's declared output, so one matcher decides whether the producer feeds the consumer.
/// </summary>
public sealed record FileSelectionSpec
{
    public string? Type { get; init; }
    public string? Location { get; init; }
    public string? Glob { get; init; }
    public string? Mask { get; init; }
    public string? SrcPath { get; init; }
}

/// <summary>
/// The one implementation of SQLFlow's file-selection semantics: how a source's location, file-name glob
/// (<c>srcFile</c>), and path mask (<c>srcPathMask</c>) decide which files are in scope, and, from that, whether a
/// file <em>producer</em> (an invoke that lands files) feeds a file <em>consumer</em> (a file ingestion). Both the
/// lineage read API ("which flow would ingest THIS file") and the declared-tier lineage collector ("which invoke
/// output feeds which ingestion") resolve through here, so "what the engine would pick up" is defined once and can
/// never drift between the two.
/// </summary>
public static class FileSelection
{
    /// <summary>The default file-name glob a reader applies when a source declares no <c>srcFile</c>, by source
    /// type. Mirrors each file reader's <c>DefaultFilePattern</c>; an unknown type matches any file.</summary>
    public static string DefaultPattern(string? type) => (type ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "csv" => "*.csv",
        "json" or "jsonl" or "ndjson" => "*.json",
        "xml" => "*.xml",
        "parquet" or "prq" => "*.parquet",
        "xls" or "xlsx" => "*.xlsx",
        _ => "*",
    };

    /// <summary>Matches a concrete file name against a glob with the exact BCL matcher the engine's cloud store
    /// uses (case-insensitive), so lineage never claims a match the engine's own selection would not make.</summary>
    public static bool NameMatchesGlob(string glob, string fileName)
        => FileSystemName.MatchesSimpleExpression(glob, fileName, ignoreCase: true);

    /// <summary>Matches a path against a source path-mask regex the way the engine does (case-insensitive, culture
    /// invariant), bounded by a short timeout and treating an invalid or runaway pattern as no match rather than
    /// letting a bad catalog value fault the caller.</summary>
    public static bool SafeRegexMatch(string pattern, string path)
    {
        try
        {
            return Regex.IsMatch(
                path, pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a file <paramref name="producer"/> feeds a file <paramref name="consumer"/>: the engine-parity link
    /// used to connect an invoke's declared output to a downstream file ingestion. A wildcard always searches
    /// within a folder, so the folder is confirmed first, then the file names are compared inside it:
    /// <list type="number">
    /// <item>Path step: a path mask on either side is applied symmetrically - a consumer mask must match the
    /// producer's landing path, and a producer mask (declared on an output) must match the consumer's watched path;
    /// with no mask the producer's folder must be the consumer's watched folder or a folder beneath it.</item>
    /// <item>File step: the producer's file-name pattern must be able to yield a name the consumer's glob accepts. A
    /// concrete producer name is tested with the engine's own glob matcher; two patterns that both carry wildcards
    /// are tested for a non-empty overlap, so neither side has to name a literal file.</item>
    /// </list>
    /// The match is conservative: anything it cannot confirm (an unresolvable location it cannot compare, a folder
    /// it cannot align) yields no link rather than a false one.
    /// </summary>
    public static bool Feeds(FileSelectionSpec producer, FileSelectionSpec consumer)
    {
        ArgumentNullException.ThrowIfNull(producer);
        ArgumentNullException.ThrowIfNull(consumer);

        var (producerDir, producerPattern) = Resolve(producer);
        var (consumerDir, consumerPattern) = Resolve(consumer);

        // Path step: a mask, when present on either side, is an authoritative path constraint (a regex over the full
        // path) and is applied symmetrically - the consumer's mask must accept the producer's landing path, and the
        // producer's mask (declared on an output) must accept the consumer's watched path. With no mask on either
        // side the producer must land in the folder the consumer watches, or a folder beneath it.
        bool pathConfirmed;
        var producerMask = string.IsNullOrWhiteSpace(producer.Mask) ? null : producer.Mask!;
        var consumerMask = string.IsNullOrWhiteSpace(consumer.Mask) ? null : consumer.Mask!;
        if (producerMask is not null || consumerMask is not null)
        {
            var producerSample = Combine(producerDir, producerPattern);
            var consumerSample = Combine(consumerDir, consumerPattern);
            pathConfirmed =
                (consumerMask is null || SafeRegexMatch(consumerMask, producerSample) || SafeRegexMatch(consumerMask, producerDir))
                && (producerMask is null || SafeRegexMatch(producerMask, consumerSample) || SafeRegexMatch(producerMask, consumerDir));
        }
        else
        {
            pathConfirmed = PathAligned(producerDir, consumerDir);
        }

        if (!pathConfirmed)
        {
            return false;
        }

        // File step, inside the confirmed folder: a concrete producer name uses the engine's glob matcher; two
        // wildcard patterns are tested for a shared match so a timestamped drop still links to a '*' watcher.
        return HasWildcard(producerPattern)
            ? GlobsOverlap(producerPattern, consumerPattern)
            : NameMatchesGlob(consumerPattern, producerPattern);
    }

    /// <summary>Whether two file-name globs (with <c>*</c> and <c>?</c> wildcards) can match a common file name.
    /// Used when both the producer and the consumer describe their files with a pattern rather than a literal name,
    /// so an invoke that drops <c>orders_*.csv</c> still links to an ingestion reading <c>*.csv</c>. Matching is
    /// case-insensitive; every character other than <c>*</c>/<c>?</c> is a literal, matching glob semantics.</summary>
    public static bool GlobsOverlap(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var x = a.ToLowerInvariant();
        var y = b.ToLowerInvariant();
        int lx = x.Length, ly = y.Length;

        // Reachability over paired positions (i into x, j into y): a state is reachable when the two patterns can
        // agree on some common prefix consumed so far. '*' advances by epsilon (matches the empty string) or by
        // staying put while consuming a shared character; the intersection is non-empty iff the paired end state
        // is reachable. O(lx * ly) states, each with a bounded number of transitions.
        var visited = new bool[lx + 1, ly + 1];
        var stack = new Stack<(int I, int J)>();
        stack.Push((0, 0));
        visited[0, 0] = true;

        while (stack.Count > 0)
        {
            var (i, j) = stack.Pop();
            if (i == lx && j == ly)
            {
                return true;
            }

            // '*' matches the empty string: advance past it without consuming a character.
            if (i < lx && x[i] == '*' && !visited[i + 1, j])
            {
                visited[i + 1, j] = true;
                stack.Push((i + 1, j));
            }

            if (j < ly && y[j] == '*' && !visited[i, j + 1])
            {
                visited[i, j + 1] = true;
                stack.Push((i, j + 1));
            }

            // Consume one character both patterns can agree on. '*'/'?' accept any character; a literal accepts
            // only itself, so two differing literals block the step. A '*' stays put (it can match more); '?' and a
            // literal advance.
            if (i < lx && j < ly)
            {
                var ax = x[i];
                var by = y[j];
                var agree = ax is '*' or '?' || by is '*' or '?' || ax == by;
                if (agree)
                {
                    var ni = ax == '*' ? i : i + 1;
                    var nj = by == '*' ? j : j + 1;
                    if (!visited[ni, nj])
                    {
                        visited[ni, nj] = true;
                        stack.Push((ni, nj));
                    }
                }
            }
        }

        return false;
    }

    /// <summary>Splits a spec into the folder it acts on and the file-name pattern within it. An explicit glob makes
    /// the whole location the folder; otherwise a location whose last segment looks like a file name (a wildcard or
    /// an extension dot) is split into parent folder and that name, and a bare folder takes the type's default
    /// pattern. Add a trailing slash to force a location to be read as a folder.</summary>
    private static (string Dir, string Pattern) Resolve(FileSelectionSpec spec)
    {
        var location = (spec.Location ?? spec.SrcPath ?? string.Empty).Trim().Replace('\\', '/');
        var glob = string.IsNullOrWhiteSpace(spec.Glob) ? null : spec.Glob!.Trim();

        if (glob is not null)
        {
            return (TrimTrailingSlash(location), glob);
        }

        if (location.Length == 0 || location.EndsWith('/'))
        {
            return (TrimTrailingSlash(location), DefaultPattern(spec.Type));
        }

        var slash = location.LastIndexOf('/');
        var last = slash >= 0 ? location[(slash + 1)..] : location;
        if (HasWildcard(last) || last.Contains('.', StringComparison.Ordinal))
        {
            var dir = slash >= 0 ? location[..slash] : string.Empty;
            return (dir, last);
        }

        return (location, DefaultPattern(spec.Type));
    }

    /// <summary>Whether the producer's folder is the consumer's watched folder or a folder beneath it (segment
    /// aware, so <c>raw/orders</c> does not spuriously match <c>raw/orders2</c>). An empty consumer folder watches
    /// everything. Azure Storage folders are folded to their scheme- and host-independent identity first, so an
    /// <c>abfss://</c> drop aligns under an <c>https://</c> watched folder naming the same container path; a
    /// non-Azure location (a local path, another cloud URL, or an unresolved <c>${...}</c> reference) is compared
    /// verbatim, so identical or nested references still align while genuinely different ones do not.</summary>
    private static bool PathAligned(string producerDir, string consumerDir)
    {
        var p = TrimTrailingSlash(Canonicalize(producerDir));
        var c = TrimTrailingSlash(Canonicalize(consumerDir));
        if (c.Length == 0)
        {
            return true;
        }

        return string.Equals(p, c, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(c + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Folds an Azure Storage folder to the same canonical identity lineage nodes use, so the URI shape a
    /// drop was written in and the shape it is read back in align on one path. A non-Azure location (or an
    /// Azure-scheme URI too malformed to parse) is returned unchanged for a verbatim comparison.</summary>
    private static string Canonicalize(string dir)
    {
        try
        {
            return AzureBlobLocation.CanonicalIdentity(dir) ?? dir;
        }
        catch (SqlFlowException)
        {
            return dir;
        }
    }

    private static string Combine(string dir, string pattern)
        => dir.Length == 0 ? pattern : $"{dir}/{pattern}";

    private static string TrimTrailingSlash(string value)
        => value.Length > 1 ? value.TrimEnd('/') : value;

    private static bool HasWildcard(string value)
        => value.Contains('*', StringComparison.Ordinal) || value.Contains('?', StringComparison.Ordinal);
}
