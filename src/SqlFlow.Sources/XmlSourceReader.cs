using System.Runtime.CompilerServices;
using System.Xml.Linq;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;
using SqlFlow.Sources.Xml;

namespace SqlFlow.Sources;

/// <summary>
/// Reads XML files by flattening each row element against a declarative, path-based configuration (the
/// modern replacement for the original <c>XmlToDataTableCode</c>). Attributes, nested elements, and
/// repeating elements become columns; namespaces are stripped by default; repeating elements explode into
/// rows when configured. Like CSV/XLS/JSON it rides <see cref="FileSourceReaderBase"/> for file selection,
/// schema evolution across files, provenance / key columns, streaming, and the post-load lifecycle - one
/// code path. The flattened columns and the streamed rows both come from one deterministic flatten.
/// </summary>
public sealed class XmlSourceReader : FileSourceReaderBase, IFlattenIntrospector
{
    public XmlSourceReader(IFileLifecycle lifecycle, IEnumerable<IFileStore> fileStores)
        : base(lifecycle, fileStores)
    {
    }

    public override bool CanHandle(string sourceType)
        => sourceType is not null && sourceType.Equals("xml", StringComparison.OrdinalIgnoreCase);

    protected override string DefaultFilePattern => "*.xml";

    protected override FileSourceOptions ReadOptions(SourceSpec source)
    {
        var meta = PreIngestionXml.FromSource(source);
        return new FileSourceOptions
        {
            SrcPath = meta.SrcPath,
            SrcFile = meta.SrcFile,
            SrcPathMask = meta.SrcPathMask,
            SearchSubDirectories = meta.SearchSubDirectories,
            InitFromFileDate = meta.InitFromFileDate,
            InitToFileDate = meta.InitToFileDate,
            IncrementalAfterDate = meta.IncrementalAfterDate,
            FileDate = FileDateSpec.FromOptions(source.Options),
            DataSetDate = DataSetDateSpec.FromOptions(source.Options),
            ReadAhead = FileSourceOptions.ParseReadAhead(source.Options, FileSourceOptions.WholeFileDefaultReadAhead),
            CopyToPath = meta.CopyToPath,
            ZipToPath = meta.ZipToPath,
            SrcDeleteIngested = meta.SrcDeleteIngested,
            SrcDeleteAtPath = meta.SrcDeleteAtPath,
            ShowPathWithFileName = meta.ShowPathWithFileName,
            IncludeFileName = meta.IncludeFileName,
            IncludeFileDate = meta.IncludeFileDate,
            IncludeFileRowDate = meta.IncludeFileRowDate,
            IncludeFileSize = meta.IncludeFileSize,
            IncludeDataSet = meta.IncludeDataSet,
            IncludeRowNumber = meta.IncludeRowNumber,
            IncludeFileLineNumber = meta.IncludeFileLineNumber,
            IncludeHashKey = meta.IncludeHashKey,
            HashKeyColumns = meta.HashKeyColumns,
            HashKeyType = meta.HashKeyType,
            IncludeConcatKey = meta.IncludeConcatKey,
            ConcatKeyColumns = meta.ConcatKeyColumns,
            ConcatKeySeparator = meta.ConcatKeySeparator,
        };
    }

    protected override async Task<IReadOnlyList<string>> ReadColumnNamesAsync(IFileStore store, FileRef file, SourceSpec source, CancellationToken ct)
    {
        var meta = PreIngestionXml.FromSource(source);
        var flattener = new XmlPathFlattener(BuildFlattenConfig(meta));
        var data = await ReadAllBytesAsync(store, file, ct).ConfigureAwait(false);
        var records = XmlRecordReader.ReadRecords(data, meta.RowXPath, meta.StripNamespacePrefixes, file.Name);
        return DiscoverColumns(records, flattener);
    }

    protected override async IAsyncEnumerable<FileLine> ReadLinesAsync(
        IFileStore store, FileRef file, SourceSpec source, [EnumeratorCancellation] CancellationToken ct)
    {
        var meta = PreIngestionXml.FromSource(source);
        var flattener = new XmlPathFlattener(BuildFlattenConfig(meta));
        var data = await ReadAllBytesAsync(store, file, ct).ConfigureAwait(false);
        var records = XmlRecordReader.ReadRecords(data, meta.RowXPath, meta.StripNamespacePrefixes, file.Name);

        // The column order comes from the discovery the schema pass already ran and cached, so the data pass is
        // one download, one parse, and one flatten per record. Only a standalone read with no prior schema pass
        // on this spec discovers the order here, from the records just parsed.
        var columns = CachedRawColumnNames(source, file) ?? DiscoverColumns(records, flattener);
        // Case-insensitive, matching SQL Server collation and the base pipeline's column union.
        var index = new Dictionary<string, int>(columns.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columns.Count; i++)
        {
            index[columns[i]] = i;
        }

        long recordOrdinal = 0;
        long outputRow = 0;
        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();
            recordOrdinal++;

            // The flattener knows the record, not which file it came from, so the file name is attached
            // here: an explode bound that trips is only actionable if the operator can find the document.
            IReadOnlyList<IReadOnlyList<KeyValuePair<string, string?>>> flattened;
            try
            {
                flattened = flattener.FlattenRows(record);
            }
            catch (SqlFlow.Core.SqlFlowException ex)
            {
                throw new SqlFlow.Core.SqlFlowException($"{file.Name} (record {recordOrdinal}): {ex.Message}", ex);
            }

            foreach (var rowPairs in flattened)
            {
                var cells = new string?[columns.Count];
                foreach (var (name, value) in rowPairs)
                {
                    if (index.TryGetValue(name, out var target))
                    {
                        cells[target] = value;
                    }
                }

                outputRow++;
                yield return new FileLine(cells, recordOrdinal, outputRow);
            }
        }
    }

    /// <summary>Builds the flatten configuration from the bound metadata, validating its option values.</summary>
    public static XmlFlattenConfig BuildFlattenConfig(PreIngestionXml meta)
    {
        if (meta.MaxRowsPerRecord < 1)
        {
            throw new SqlFlow.Core.SqlFlowException(
                $"Invalid maxRowsPerRecord '{meta.MaxRowsPerRecord}'. Use a positive integer.");
        }

        if (meta.MaxDepth < 1)
        {
            throw new SqlFlow.Core.SqlFlowException($"Invalid maxDepth '{meta.MaxDepth}'. Use a positive integer.");
        }

        return new XmlFlattenConfig
        {
            RowXPath = meta.RowXPath ?? string.Empty,
            IncludePaths = XmlFlattenConfig.SplitPaths(meta.IncludePaths),
            ExcludePaths = XmlFlattenConfig.SplitPaths(meta.ExcludePaths),
            XmlPaths = XmlFlattenConfig.SplitPaths(meta.XmlPaths),
            ExplodePaths = XmlFlattenConfig.SplitPaths(meta.ExplodePaths),
            PathAliasColumns = XmlFlattenConfig.ParsePathAliases(meta.PathAliases),
            ColumnMappings = XmlFlattenConfig.ParseColumnMappings(meta.ColumnMappings),
            MaxDepth = meta.MaxDepth,
            MaxRowsPerRecord = meta.MaxRowsPerRecord,
            Separator = string.IsNullOrEmpty(meta.Separator) ? "_" : meta.Separator,
            RepeatHandling = XmlFlattenConfig.ParseRepeatHandling(meta.RepeatHandling),
            JoinSeparator = meta.JoinSeparator,
            IncludeAttributes = meta.IncludeAttributes,
            AttributePrefix = meta.AttributePrefix ?? "@",
            TrimText = meta.TrimText,
        };
    }

    /// <summary>Scans the source and reports its XPath structure, the derived flatten formula, and the options to run it.</summary>
    public async Task<FlattenIntrospection> IntrospectAsync(SourceSpec source, int maxFiles, int maxRecords, int maxDepth, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var meta = PreIngestionXml.FromSource(source);
        var depth = maxDepth < 1 ? 10 : maxDepth;

        // Resolve the row grain once (explicit rowXPath, else the detected record anchor, else the engine
        // default) and use it for the scan, the derived formula, and the reported options so all three agree.
        var rowXPath = await ResolveDiscoveryRowXPathAsync(source, meta, maxFiles, ct).ConfigureAwait(false);
        var autoDetected = string.IsNullOrWhiteSpace(meta.RowXPath) && !string.IsNullOrEmpty(rowXPath);
        var config = BuildFlattenConfig(meta) with { RowXPath = rowXPath };
        var flattener = new XmlPathFlattener(config);

        // One scan yields both views: the path inventory (every addressable XPath, for `paths`) and the
        // flattener's own resolved schema columns (for the formula, which is therefore exactly what the load
        // produces). The inventory uses the requested inspection depth; the formula honors the flatten's own
        // MaxDepth because it comes straight from the flattener.
        var (filesScanned, perRecord) = await ScanRecordsAsync(
            source, meta, rowXPath, maxFiles, maxRecords,
            record => (Paths: XmlPathInventoryBuilder.ExtractTypedPaths(record, depth), Columns: flattener.SchemaColumns(record)),
            ct).ConfigureAwait(false);

        var inventory = XmlPathInventoryBuilder.Build(perRecord.Select(r => r.Paths)) with { FilesScanned = filesScanned };
        var formula = XmlFlattenFormulaBuilder.FromRecords(perRecord.Select(r => r.Columns));

        var paths = inventory.Paths
            .Select(p => new SchemaPath(p.Path, MapKind(p.Kind), p.RecordCount, p.Kind == XmlNodeKind.Object ? string.Empty : config.ColumnName(p.Path)))
            .ToList();
        var columns = formula.Columns.Select(c => new SchemaColumn(c.Name, c.SourcePath, c.IsLargeText)).ToList();

        return new FlattenIntrospection(
            "xml",
            new SchemaInventory(filesScanned, inventory.RecordsScanned, paths),
            new SchemaFormula(columns, formula.CollisionMappings),
            BuildOptions(meta, rowXPath))
        {
            AutoDetectedGrain = autoDetected ? rowXPath : null,
        };
    }

    /// <summary>
    /// Resolves the row XPath the discovery scan should select records with. An explicit rowXPath is
    /// authoritative and returned as-is. Otherwise the document roots are sampled and the statistics-driven
    /// record-anchor detector proposes the outermost repeating element (an RSS feed resolves to
    /// <c>/rss/channel/item</c>); when nothing repeats, the empty default (each direct child of the root)
    /// stands. This only shapes discovery: a load still selects records with whatever rowXPath the config carries.
    /// </summary>
    private async Task<string> ResolveDiscoveryRowXPathAsync(SourceSpec source, PreIngestionXml meta, int maxFiles, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(meta.RowXPath))
        {
            return meta.RowXPath;
        }

        var detector = new XmlRecordAnchorDetector();
        var (store, files) = await ResolveFilesAsync(source, ct).ConfigureAwait(false);
        var fileLimit = maxFiles > 0 ? maxFiles : int.MaxValue;
        var filesScanned = 0;

        foreach (var file in files)
        {
            if (filesScanned >= fileLimit)
            {
                break;
            }

            filesScanned++;
            var data = await ReadAllBytesAsync(store, file, ct).ConfigureAwait(false);
            var root = XmlRecordReader.ReadDocumentRoot(data, meta.StripNamespacePrefixes, file.Name);
            if (root is not null)
            {
                detector.Accumulate(root);
            }
        }

        return detector.Detect() ?? string.Empty;
    }

    private async Task<(int FilesScanned, List<T> Items)> ScanRecordsAsync<T>(
        SourceSpec source, PreIngestionXml meta, string rowXPath, int maxFiles, int maxRecords, Func<XElement, T> project, CancellationToken ct)
    {
        var (store, files) = await ResolveFilesAsync(source, ct).ConfigureAwait(false);
        var items = new List<T>();
        var fileLimit = maxFiles > 0 ? maxFiles : int.MaxValue;
        var filesScanned = 0;
        var recordsScanned = 0;

        foreach (var file in files)
        {
            if (filesScanned >= fileLimit || (maxRecords > 0 && recordsScanned >= maxRecords))
            {
                break;
            }

            filesScanned++;
            var data = await ReadAllBytesAsync(store, file, ct).ConfigureAwait(false);

            foreach (var record in XmlRecordReader.ReadRecords(data, rowXPath, meta.StripNamespacePrefixes, file.Name))
            {
                if (maxRecords > 0 && recordsScanned >= maxRecords)
                {
                    break;
                }

                recordsScanned++;
                items.Add(project(record));
            }
        }

        return (filesScanned, items);
    }

    private static SchemaPathKind MapKind(XmlNodeKind kind) => kind switch
    {
        XmlNodeKind.Object => SchemaPathKind.Container,
        XmlNodeKind.Array => SchemaPathKind.Repeating,
        _ => SchemaPathKind.Value,
    };

    private static IReadOnlyList<KeyValuePair<string, string>> BuildOptions(PreIngestionXml meta, string rowXPath)
    {
        var options = new List<KeyValuePair<string, string>>();
        if (!string.IsNullOrWhiteSpace(rowXPath))
        {
            options.Add(new KeyValuePair<string, string>("rowXPath", rowXPath));
        }

        options.Add(new KeyValuePair<string, string>("separator", meta.Separator));
        options.Add(new KeyValuePair<string, string>("repeatHandling", meta.RepeatHandling));

        if (!meta.IncludeAttributes)
        {
            options.Add(new KeyValuePair<string, string>("includeAttributes", "false"));
        }

        if (meta.AttributePrefix != "@")
        {
            options.Add(new KeyValuePair<string, string>("attributePrefix", meta.AttributePrefix));
        }

        AddIfSet(options, "includePaths", meta.IncludePaths);
        AddIfSet(options, "excludePaths", meta.ExcludePaths);
        AddIfSet(options, "xmlPaths", meta.XmlPaths);
        AddIfSet(options, "explodePaths", meta.ExplodePaths);
        AddIfSet(options, "pathAliases", meta.PathAliases);
        AddIfSet(options, "columnMappings", meta.ColumnMappings);
        return options;
    }

    private static void AddIfSet(List<KeyValuePair<string, string>> options, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            options.Add(new KeyValuePair<string, string>(key, value));
        }
    }

    private static List<string> DiscoverColumns(IReadOnlyList<XElement> records, XmlPathFlattener flattener)
    {
        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            foreach (var (name, _) in flattener.Flatten(record))
            {
                if (seen.Add(name))
                {
                    order.Add(name);
                }
            }
        }

        return order;
    }

    private static async Task<byte[]> ReadAllBytesAsync(IFileStore store, FileRef file, CancellationToken ct)
    {
        await using var stream = await store.OpenReadAsync(file, ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }
}
