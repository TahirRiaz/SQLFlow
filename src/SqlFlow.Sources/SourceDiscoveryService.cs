using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;

namespace SqlFlow.Sources;

/// <summary>What the caller wants discovered: a file/folder location plus the optional format, folder scan controls,
/// record-grain override (JSON/XML), and sample bounds. A blank <see cref="Format"/> means auto-detect.</summary>
public sealed record SourceDiscoveryRequest(
    string Location, string? Format, string? Pattern, bool Recursive, string? RootPath,
    int MaxFiles, int MaxRecords, int MaxDepth, string? DefaultColumnType = null);

/// <summary>One discovered output column and, for a flattened nested source, the path it came from.</summary>
public sealed record DiscoveredColumn(string Name, string SqlType, bool Nullable, string? SourcePath);

/// <summary>
/// The unified discovery outcome. <see cref="Mode"/> is "flatten" for nested sources (JSON/XML: the response
/// carries <see cref="Paths"/> and a record grain) or "columnar" for tabular sources (CSV/Excel/Parquet: just
/// <see cref="Columns"/>). Both modes always carry the runnable <see cref="GeneratedYaml"/>.
/// </summary>
public sealed record SourceDiscoveryResult(
    string Mode, string SourceType, string DetectionConfidence, IReadOnlyList<string> DetectionEvidence,
    int FilesScanned, int RecordsScanned, string? AutoDetectedGrain, bool SchemaDrift,
    IReadOnlyList<SchemaPath> Paths, IReadOnlyList<DiscoveredColumn> Columns,
    IReadOnlyList<KeyValuePair<string, string>> Options, string GeneratedYaml);

/// <summary>
/// One entry point that discovers any supported file source and generates its ingestion YAML. It resolves the
/// format (explicit, then extension, then content sniffing), then dispatches: a nested source (JSON/XML) goes to the
/// flatten introspector for path discovery and a flatten formula; a tabular source (CSV/Excel/Parquet) has its
/// column schema read from a representative file and rendered as a straight columnar flow. The reader abstraction
/// (<see cref="ISourceReader"/>) is what makes "one command, any format" possible - every format already exposes its
/// schema through the same interface.
/// </summary>
public sealed class SourceDiscoveryService
{
    private readonly IReadOnlyList<ISourceReader> _readers;
    private readonly IReadOnlyList<IFileStore> _fileStores;
    private readonly ISqlTypeMapper _typeMapper;

    public SourceDiscoveryService(IEnumerable<ISourceReader> readers, IEnumerable<IFileStore> fileStores, ISqlTypeMapper typeMapper)
    {
        ArgumentNullException.ThrowIfNull(readers);
        ArgumentNullException.ThrowIfNull(fileStores);
        _readers = readers.ToList();
        _fileStores = fileStores.ToList();
        _typeMapper = typeMapper ?? throw new ArgumentNullException(nameof(typeMapper));
    }

    /// <summary>Discovers the source at <paramref name="request"/> and returns its structure plus generated YAML.
    /// When <paramref name="progress"/> is supplied, each phase (format, delimiter, schema, sampling) is reported so a
    /// caller can stream the operation live. Throws <see cref="SqlFlowException"/> for a caller-fixable problem (bad
    /// location, unknown format, empty selection).</summary>
    public async Task<SourceDiscoveryResult> DiscoverAsync(
        SourceDiscoveryRequest request, IDiscoveryProgress? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var location = request.Location?.Trim() ?? string.Empty;
        if (location.Length == 0)
        {
            throw new SqlFlowException("A discover requires a non-blank location (a file, folder, or URI).");
        }

        await ReportAsync(progress, "start", $"Discovering source at '{location}'.", ct).ConfigureAwait(false);

        var store = _fileStores.FirstOrDefault(s => s.CanHandle(location))
            ?? throw new SqlFlowException($"No file store handles location '{location}'.");

        await ReportAsync(progress, "format", "Detecting the source format.", ct).ConfigureAwait(false);
        var (type, confidence, evidence, csvDelimiter, delimiterResolved) =
            await ResolveFormatAsync(store, location, request, progress, ct).ConfigureAwait(false);
        await ReportAsync(progress, "format",
            $"Format: {type} ({confidence}). {string.Join("; ", evidence)}", ct).ConfigureAwait(false);

        var (specType, options) = FlattenSourceSpec.BuildBase(location, type, request.Pattern, request.Recursive, request.RootPath);
        if (csvDelimiter is not null && specType == "csv")
        {
            options["delimiter"] = csvDelimiter;
        }

        var spec = new SourceSpec { Type = specType, Location = location, Options = options };

        var introspector = FlattenSourceSpec.IntrospectorFor(_readers, specType);
        return introspector is not null
            ? await FlattenAsync(introspector, spec, request, confidence, evidence, progress, ct).ConfigureAwait(false)
            : await ColumnarAsync(spec, store, request, confidence, evidence, delimiterResolved, progress, ct).ConfigureAwait(false);
    }

    /// <summary>Forwards one progress phase to the sink when one is supplied; a no-op otherwise.</summary>
    private static Task ReportAsync(IDiscoveryProgress? progress, string step, string message, CancellationToken ct)
        => progress?.ReportAsync(step, message, ct) ?? Task.CompletedTask;

    /// <summary>
    /// Resolves the source type in priority order: explicit format, then the location's own extension, then a listed
    /// representative file's extension, then content sniffing of that file's head. <c>DelimiterResolved</c> is true
    /// only when the delimiter is already settled (an explicit TSV, or a content sniff that profiled the bytes); for a
    /// CSV resolved from an explicit format or a file extension it is false, so the columnar reader sniffs the actual
    /// sample and a semicolon/tab/pipe file is not read as a single comma column.
    /// </summary>
    private static async Task<(string Type, string Confidence, List<string> Evidence, string? CsvDelimiter, bool DelimiterResolved)> ResolveFormatAsync(
        IFileStore store, string location, SourceDiscoveryRequest request, IDiscoveryProgress? progress, CancellationToken ct)
    {
        // An explicit "tsv" is comma-CSV's tab-delimited sibling: same reader, a pinned delimiter.
        var explicitFormat = request.Format?.Trim().ToLowerInvariant();
        if (explicitFormat == "tsv")
        {
            return ("csv", "explicit", ["format specified explicitly: TSV (tab-delimited)"], "\t", true);
        }

        if (!string.IsNullOrWhiteSpace(explicitFormat))
        {
            var (pinnedType, _) = FlattenSourceSpec.BuildBase(location, explicitFormat, request.Pattern, request.Recursive, null);
            // An explicitly-chosen CSV still needs its delimiter sniffed from the data, so leave it unresolved.
            return (pinnedType, "explicit", [$"format specified explicitly: {pinnedType}"], null, false);
        }

        // The location's own last segment often names the format (a single file, or a URI ending in .json).
        var fromLocation = SourceFormatDetector.TypeFromExtension(FlattenSourceSpec.UriLastSegment(location));
        if (fromLocation is not null)
        {
            return (fromLocation, "extension", [$"file extension of the location ({fromLocation})"], null, false);
        }

        // Otherwise list a representative file and try its extension, then sniff its bytes.
        await ReportAsync(progress, "scan", "Listing a representative file under the location.", ct).ConfigureAwait(false);
        var sample = await FirstFileAsync(store, location, request.Pattern ?? "*", request.Recursive, ct).ConfigureAwait(false)
            ?? throw new SqlFlowException(EmptySelectionMessage(location, request.Pattern));

        var fromSample = SourceFormatDetector.TypeFromExtension(sample.Name);
        if (fromSample is not null)
        {
            return (fromSample, "extension", [$"file extension of '{sample.Name}' ({fromSample})"], null, false);
        }

        var head = await ReadHeadAsync(store, sample, SourceFormatDetector.HeadSampleBytes, ct).ConfigureAwait(false);
        var detection = SourceFormatDetector.FromContent(head, sample.Name);
        if (detection.Type is null)
        {
            throw new SqlFlowException(
                $"Could not determine the format of '{sample.Name}'"
                + (detection.Evidence.Count > 0 ? $" ({string.Join("; ", detection.Evidence)})" : string.Empty)
                + ". Choose a format explicitly.");
        }

        var evidence = new List<string> { $"content-detected from '{sample.Name}'" };
        evidence.AddRange(detection.Evidence);
        return (detection.Type, detection.Confidence, evidence, detection.CsvDelimiter, true);
    }

    private static async Task<SourceDiscoveryResult> FlattenAsync(
        IFlattenIntrospector introspector, SourceSpec spec, SourceDiscoveryRequest request,
        string confidence, List<string> evidence, IDiscoveryProgress? progress, CancellationToken ct)
    {
        await ReportAsync(progress, "introspect", "Sampling records and discovering the path structure.", ct).ConfigureAwait(false);
        var introspection = await introspector
            .IntrospectAsync(spec, request.MaxFiles, request.MaxRecords, request.MaxDepth, ct).ConfigureAwait(false);
        var inventory = introspection.Inventory;
        await ReportAsync(progress, "introspect",
            $"Scanned {inventory.FilesScanned} file(s), {inventory.RecordsScanned} record(s); found {inventory.Paths.Count} path(s).",
            ct).ConfigureAwait(false);

        var columnType = ResolveColumnType(request);
        var maxType = FlattenFlowYaml.MaxVariant(columnType);
        var columns = introspection.Formula.Columns
            .Select(c => new DiscoveredColumn(c.Name, c.IsLargeText ? maxType : columnType, true, c.SourcePath))
            .ToList();

        return new SourceDiscoveryResult(
            "flatten", introspection.SourceType, confidence, evidence,
            inventory.FilesScanned, inventory.RecordsScanned, introspection.AutoDetectedGrain,
            inventory.Paths.Any(p => p.RecordCount < inventory.RecordsScanned),
            inventory.Paths, columns, introspection.Options, FlattenFlowYaml.Build(spec, introspection, columnType));
    }

    private async Task<SourceDiscoveryResult> ColumnarAsync(
        SourceSpec spec, IFileStore store, SourceDiscoveryRequest request,
        string confidence, List<string> evidence, bool delimiterResolved, IDiscoveryProgress? progress, CancellationToken ct)
    {
        var reader = _readers.FirstOrDefault(r => r.CanHandle(spec.Type))
            ?? throw new SqlFlowException(
                $"No reader handles '{spec.Type}'. Supported: JSON, XML, CSV, Excel (xls/xlsx), Parquet.");

        // Read the schema from one representative file (bounded), not the whole folder: a raw landing folder's files
        // share a schema, and a real run unions and NULL-fills any drift at load time anyway.
        var effectivePattern = request.Pattern ?? FlattenSourceSpec.DefaultPatternFor(spec.Type);
        await ReportAsync(progress, "scan", $"Selecting a representative file matching '{effectivePattern}'.", ct).ConfigureAwait(false);
        var sample = await FirstFileAsync(store, spec.Location!, effectivePattern, request.Recursive, ct).ConfigureAwait(false)
            ?? throw new SqlFlowException(EmptySelectionMessage(spec.Location!, effectivePattern));

        // Sniff the delimiter from the actual sample when the format was pinned or resolved from a .csv extension (the
        // content detector never ran), so a semicolon/tab/pipe file is not read as one comma-delimited column. The
        // sniffed delimiter is folded into both the schema probe and the generated YAML's source.options.
        if (spec.Type == "csv" && !delimiterResolved && !spec.Options.ContainsKey("delimiter"))
        {
            await ReportAsync(progress, "delimiter", $"Sniffing the CSV delimiter from '{sample.Name}'.", ct).ConfigureAwait(false);
            var head = await ReadHeadAsync(store, sample, SourceFormatDetector.HeadSampleBytes, ct).ConfigureAwait(false);
            var (option, note) = SourceFormatDetector.DetectCsvDelimiter(head);
            evidence.Add(note);
            await ReportAsync(progress, "delimiter", note, ct).ConfigureAwait(false);
            if (option is not null)
            {
                var withDelimiter = new Dictionary<string, string?>(spec.Options, StringComparer.OrdinalIgnoreCase)
                {
                    ["delimiter"] = option,
                };
                spec = spec with { Options = withDelimiter };
            }
        }

        // Probe the sample with provenance/key columns off so the reported columns are the source's own, not the
        // generated _DW columns; the rendered YAML keeps the provenance defaults a real ingestion uses.
        var probeOptions = new Dictionary<string, string?>(spec.Options, StringComparer.OrdinalIgnoreCase);
        foreach (var key in FlattenSourceSpec.ProvenanceOptionKeys)
        {
            probeOptions[key] = "false";
        }

        await ReportAsync(progress, "schema", $"Reading the column schema from '{sample.Name}'.", ct).ConfigureAwait(false);
        var probeSpec = spec with { Location = sample.Path, Options = probeOptions };
        IReadOnlyList<SourceColumn> sourceColumns;
        try
        {
            sourceColumns = await reader.GetColumnsAsync(probeSpec, ct).ConfigureAwait(false);
        }
        finally
        {
            await reader.CompleteAsync(probeSpec, ct).ConfigureAwait(false);
        }

        await ReportAsync(progress, "schema", $"Discovered {sourceColumns.Count} column(s).", ct).ConfigureAwait(false);

        var columnType = ResolveColumnType(request);
        var columns = sourceColumns
            .Select(c => new DiscoveredColumn(c.Name, _typeMapper.Map(c, null, columnType).SqlType, c.IsNullable, null))
            .ToList();

        var yaml = FlattenFlowYaml.BuildColumnar(spec, spec.Type, sourceColumns, _typeMapper, filesScanned: 1, columnType);
        var options = spec.Options.Select(o => new KeyValuePair<string, string>(o.Key, o.Value ?? string.Empty)).ToList();

        return new SourceDiscoveryResult(
            "columnar", spec.Type, confidence, evidence,
            1, 0, null, false, [], columns, options, yaml);
    }

    /// <summary>The landing column type for a request: the caller's choice, or SQLFlow's lean <c>varchar(255)</c> default.</summary>
    private static string ResolveColumnType(SourceDiscoveryRequest request)
        => string.IsNullOrWhiteSpace(request.DefaultColumnType) ? FlattenFlowYaml.DefaultColumnType : request.DefaultColumnType.Trim();

    /// <summary>The representative file (name-ordered) under a location matching the pattern, or null when nothing
    /// matches. Prefers the first non-empty file: a zero-byte entry is an ADLS Gen2 directory marker (which the broad
    /// "*" auto-detect pattern matches, unlike a "*.csv" pattern) or an empty file, and neither can inform format
    /// detection or a schema read. Falls back to name order only when every match is empty, so the empty-selection
    /// path still reports accurately.</summary>
    private static async Task<FileRef?> FirstFileAsync(
        IFileStore store, string location, string pattern, bool recursive, CancellationToken ct)
    {
        var files = await store
            .ListAsync(location, new FileDiscovery { Pattern = pattern, Recursive = recursive }, ct).ConfigureAwait(false);
        var ordered = files.OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
        return ordered.FirstOrDefault(f => f.Size > 0) ?? ordered.FirstOrDefault();
    }

    private static async Task<byte[]> ReadHeadAsync(IFileStore store, FileRef file, int max, CancellationToken ct)
    {
        await using var stream = await store.OpenReadAsync(file, ct).ConfigureAwait(false);
        var buffer = new byte[max];
        var total = 0;
        while (total < max)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, max - total), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total == max ? buffer : buffer[..total];
    }

    private static string EmptySelectionMessage(string location, string? pattern)
        => pattern is null
            ? $"No files found under '{location}'."
            : $"No files under '{location}' match pattern '{pattern}'.";
}
