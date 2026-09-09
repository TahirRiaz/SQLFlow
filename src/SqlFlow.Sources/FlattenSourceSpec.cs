using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;

namespace SqlFlow.Sources;

/// <summary>
/// Builds the <see cref="SourceSpec"/> the <c>paths</c>, <c>flatten</c>, and <c>discover</c> features scan from
/// a bare file or folder location. This is the one place that decides a location's format (from its extension or
/// the folder pattern), whether it names a folder, and how the record-grain override maps onto the format's option
/// key - so the CLI and the control-plane endpoint resolve a location identically instead of drifting apart.
/// </summary>
public static class FlattenSourceSpec
{
    /// <summary>
    /// The provenance option keys a data preview turns off for a clean dump. Kept here (not in the CLI) so the one
    /// caller that suppresses provenance and the YAML writer that emits them agree on the exact set.
    /// </summary>
    public static readonly string[] ProvenanceOptionKeys =
    [
        "includeFileName", "includeFileDate", "includeFileRowDate",
        "includeFileSize", "includeDataSet", "includeRowNumber",
    ];

    /// <summary>
    /// Derives the source type and the base scan options (folder glob, sub-directory recursion, record-grain
    /// override) for a location. Pass <paramref name="format"/> to pin the type (<c>json</c>/<c>ndjson</c>/
    /// <c>jsonl</c>/<c>xml</c>); leave it null to infer from the extension (file) or pattern (folder). The returned
    /// dictionary is mutable so a caller threading extra flatten rules layers them onto the same options.
    /// </summary>
    public static (string Type, Dictionary<string, string?> Options) BuildBase(
        string location, string? format, string? pattern, bool recursive, string? rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // A cloud URI cannot be probed with Directory.Exists, so treat it as a folder when the caller passes a
        // pattern or the location has no file extension (a container/prefix); otherwise it is a single blob.
        var isUri = location.Contains("://", StringComparison.Ordinal);
        var treatAsFolder = Directory.Exists(location)
            || (isUri && (pattern is not null || !UriPathLooksLikeFile(location)));

        string type;
        if (treatAsFolder)
        {
            type = NormalizeFormat(format)
                ?? (pattern is not null ? SourceFormatDetector.TypeFromExtension(pattern) : null)
                ?? "json";
            options["srcFile"] = pattern ?? DefaultPatternFor(type);
            if (recursive)
            {
                options["searchSubDirectories"] = "true";
            }
        }
        else
        {
            type = NormalizeFormat(format)
                ?? SourceFormatDetector.TypeFromExtension(UriLastSegment(location))
                ?? "json";
        }

        // The record grain is a JSON/XML concept only; a delimited or columnar source has no rootPath/rowXPath.
        if (!string.IsNullOrWhiteSpace(rootPath) && type is "json" or "ndjson" or "jsonl" or "xml")
        {
            options[type == "xml" ? "rowXPath" : "rootPath"] = rootPath.Trim();
        }

        return (type, options);
    }

    /// <summary>Builds a ready-to-scan <see cref="SourceSpec"/> for a location; see <see cref="BuildBase"/>.</summary>
    public static SourceSpec Build(string location, string? format, string? pattern, bool recursive, string? rootPath)
    {
        var (type, options) = BuildBase(location, format, pattern, recursive, rootPath);
        return new SourceSpec { Type = type, Location = location, Options = options };
    }

    /// <summary>The introspector that handles a source type, or null when none does (only JSON and XML do today).</summary>
    public static IFlattenIntrospector? IntrospectorFor(IEnumerable<ISourceReader> readers, string type)
    {
        ArgumentNullException.ThrowIfNull(readers);
        return readers.OfType<IFlattenIntrospector>().FirstOrDefault(r => r.CanHandle(type));
    }

    /// <summary>The last path segment of a URI (after the final '/', with any query/fragment dropped).</summary>
    public static string UriLastSegment(string path)
    {
        var end = path.IndexOfAny(['?', '#']);
        var clean = end < 0 ? path : path[..end];
        var slash = clean.LastIndexOf('/');
        return slash < 0 ? clean : clean[(slash + 1)..];
    }

    /// <summary>True when a URI's last segment carries a recognized source-file extension (so it names a file, not a prefix).</summary>
    public static bool UriPathLooksLikeFile(string path)
    {
        var ext = Path.GetExtension(UriLastSegment(path)).TrimStart('.').ToLowerInvariant();
        return ext is "json" or "ndjson" or "jsonl" or "xml";
    }

    /// <summary>The default folder glob for a source type, e.g. "*.csv" or "*.parquet".</summary>
    public static string DefaultPatternFor(string type) => type switch
    {
        "xml" => "*.xml",
        "csv" => "*.csv",
        "xls" => "*.xls",
        "xlsx" => "*.xlsx",
        "parquet" => "*.parquet",
        "ndjson" => "*.ndjson",
        "jsonl" => "*.jsonl",
        _ => "*.json",
    };

    /// <summary>Normalizes an explicit format to a supported reader source type, or null to fall back to inference.</summary>
    private static string? NormalizeFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return null;
        }

        return format.Trim().ToLowerInvariant() switch
        {
            "xml" => "xml",
            "ndjson" => "ndjson",
            "jsonl" => "jsonl",
            "json" => "json",
            "csv" or "tsv" or "txt" => "csv",
            "xls" => "xls",
            "xlsx" or "xlsm" => "xlsx",
            "parquet" or "parq" or "prq" => "parquet",
            _ => null,
        };
    }
}
