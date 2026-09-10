using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Export;
using SqlFlow.Core.Ingestion;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Yaml;

/// <summary>
/// A parsed export document (flowType: exp): the flow itself plus the document-local connection registry it
/// declares. The connections become an in-memory data-source store, so the flow runs through the exact same
/// resolver and runner as full mode, with no control database anywhere.
/// </summary>
public sealed record ExportDocument
{
    public required ExportFlow Flow { get; init; }

    /// <summary>The document's named connections (the <c>connections:</c> block plus any synthesized from a
    /// direct <c>connection:</c> on the source).</summary>
    public required IReadOnlyList<DataSource> Connections { get; init; }

    /// <summary>The document's named invokes (the <c>invokes:</c> block), referenced by postInvoke.</summary>
    public IReadOnlyList<Core.Invoke.InvokeDefinition> Invokes { get; init; } = [];

    /// <summary>The document's named Azure service principals (the <c>servicePrincipals:</c> block).</summary>
    public IReadOnlyList<ServicePrincipalProfile> ServicePrincipals { get; init; } = [];
}

/// <summary>
/// Loads an export flow (a SQL Server table or view written to CSV or Parquet files) from YAML. YamlDotNet
/// handles the grammar; this class is the mapping/validation layer that turns the parsed document into a
/// validated <see cref="ExportDocument"/>. The YAML default for <c>export.by</c> is <c>full</c> (one file),
/// unlike the legacy table default of day chunking, so the smallest document needs no chunk policy at all;
/// the other defaults follow the model's documented legacy defaults.
/// </summary>
public sealed class YamlExportFlowLoader
{
    private const string SourceConnectionName = "source";

    // The unquoted-scalar option types the invoke parameters block (a plain true/42 stays a boolean/number,
    // a quoted scalar a string); every typed DTO property is unaffected by it.
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .WithAttemptingUnquotedStringTypeDeserialization()
        .Build();

    public ExportDocument LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Pipeline file not found: '{path}'.");
        }

        return Parse(File.ReadAllText(path), path);
    }

    public ExportDocument Parse(string yaml, string source = "<inline>")
    {
        ExportYaml? dto;
        try
        {
            dto = _deserializer.Deserialize<ExportYaml>(yaml);
        }
        catch (YamlException ex)
        {
            throw new FlowValidationException($"{source}: invalid YAML - {ex.Message}", ex);
        }

        if (dto is null)
        {
            throw new FlowValidationException($"{source}: the document is empty.");
        }

        return Map(dto, source);
    }

    private static ExportDocument Map(ExportYaml y, string source)
    {
        var name = YamlDocumentParts.RequireFlowName(y.Name, "an export flow", source);
        var connections = YamlDocumentParts.MapConnections(y.Connections, source);

        var sourceYaml = y.Source ?? throw new FlowValidationException($"{source}: 'source' is required.");
        var targetYaml = y.Target ?? throw new FlowValidationException($"{source}: 'target' is required.");

        var sourceServer = YamlDocumentParts.ResolveEndpointConnection(
            sourceYaml.Server, sourceYaml.Connection, sourceYaml.Provider,
            "source", SourceConnectionName, connections, source);

        // The export read is T-SQL (bracketed SELECTs, DATEADD windows, MAX probes), so the source is SQL
        // Server by design; a foreign source is a configuration error caught at parse time, not deep in the run.
        YamlDocumentParts.RequireSqlServerConnection(connections, sourceServer, "source", "an export flow's source", source);

        var sourceObject = ParseSourceObject(sourceYaml, source);
        var (exportBy, exportSize, dateColumn, keyColumn, fromDate, toDate, threads) = MapChunkPolicy(y.Export, source);
        var target = MapTarget(targetYaml, source);
        var servicePrincipals = YamlInvokeParts.MapServicePrincipals(y.ServicePrincipals, source);
        var invokes = YamlInvokeParts.MapInvokes(y.Invokes, servicePrincipals, source);

        var flow = new ExportFlow
        {
            FlowId = YamlDocumentParts.StableFlowId(name),
            SysAlias = name,
            Batch = YamlDocumentParts.NullIfBlank(y.Batch),
            Lifecycle = YamlDocumentParts.ParseLifecycle(y.Lifecycle, source),
            SrcServer = sourceServer,
            Source = sourceObject,
            SrcWithHint = YamlDocumentParts.NullIfBlank(sourceYaml.WithHint)?.Trim(),
            SrcFilter = NormalizeFilter(sourceYaml.Filter, source),
            IncrementalColumn = keyColumn,
            DateColumn = dateColumn,
            FromDate = fromDate,
            ToDate = toDate,
            ExportBy = exportBy,
            ExportSize = exportSize,
            TrgPath = target.Path,
            TrgFileName = target.FileName,
            TrgFiletype = target.FileType,
            TrgEncoding = target.Encoding,
            CompressionType = target.Compression,
            ColumnDelimiter = target.Delimiter,
            TextQualifier = target.TextQualifier,
            AddTimeStampToFileName = target.AddTimestamp,
            Subfolderpattern = target.SubfolderPattern,
            TrgValueFormat = target.ValueFormat,
            ZipTrg = target.Zip,
            NoOfThreads = threads,
            OnErrorResume = y.OnErrorResume ?? true,
            PostInvokeAlias = YamlInvokeParts.ResolveHookAlias(y.PostInvoke, "postInvoke", invokes, source),
        };

        return new ExportDocument
        {
            Flow = flow,
            Connections = connections.Values.ToList(),
            Invokes = invokes.Values.ToList(),
            ServicePrincipals = servicePrincipals.Values.ToList(),
        };
    }

    private static Core.Ingestion.RelationalObject ParseSourceObject(ExportSourceYaml endpoint, string source)
    {
        var raw = YamlDocumentParts.NullIfBlank(endpoint.Object) ?? YamlDocumentParts.NullIfBlank(endpoint.Table)
            ?? throw new FlowValidationException(
                $"{source}: 'source.object' is required (a three-part name like Database.Schema.Table).");

        return YamlDocumentParts.ParseQualifiedObject(raw, "source.object", source);
    }

    /// <summary>The chunk policy: how the export is split into files. The YAML tokens are words (full, day,
    /// month, key); the model carries the legacy letters (F, D, M, K). The cross-field requirements (a date
    /// chunk needs its date column, a key chunk its key column) fail here, not in the planner mid-run.</summary>
    private static (string By, int Size, string? DateColumn, string? KeyColumn, DateOnly? FromDate, DateOnly? ToDate, int Threads)
        MapChunkPolicy(ExportChunkYaml? y, string source)
    {
        var by = ParseExportBy(y?.By, source);

        var size = y?.Size ?? 1;
        if (size < 1)
        {
            throw new FlowValidationException($"{source}: 'export.size' must be at least 1, got {size}.");
        }

        // The planner brackets the chunk column itself, so an author who writes it bracketed ([CalendarID],
        // the form legacy metadata used) gets the same SELECT as one who writes it bare.
        var dateColumn = Column(y?.DateColumn);
        var keyColumn = Column(y?.KeyColumn);
        if (by is "D" or "M" && dateColumn is null)
        {
            throw new FlowValidationException(
                $"{source}: 'export.dateColumn' is required when 'export.by' is {(by == "D" ? "day" : "month")}.");
        }

        if (by is "K" && keyColumn is null)
        {
            throw new FlowValidationException($"{source}: 'export.keyColumn' is required when 'export.by' is key.");
        }

        var fromDate = YamlDocumentParts.ParseDate(y?.FromDate, "export.fromDate", source);
        var toDate = YamlDocumentParts.ParseDate(y?.ToDate, "export.toDate", source);
        if (fromDate is not null && toDate is not null && fromDate > toDate)
        {
            throw new FlowValidationException(
                $"{source}: 'export.fromDate' ({fromDate:yyyy-MM-dd}) is after 'export.toDate' ({toDate:yyyy-MM-dd}).");
        }

        var threads = y?.Threads ?? 0;
        if (threads < 0)
        {
            throw new FlowValidationException($"{source}: 'export.threads' must be 0 (default) or more, got {threads}.");
        }

        return (by, size, dateColumn, keyColumn, fromDate, toDate, threads);
    }

    private static string? Column(string? value)
        => YamlDocumentParts.NullIfBlank(value) is { } name ? IngestionText.Unbracket(name) : null;

    private static string ParseExportBy(string? value, string source)
        => value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "full" or "f" => "F",
            "day" or "d" => "D",
            "month" or "m" => "M",
            "key" or "k" => "K",
            _ => throw new FlowValidationException(
                $"{source}: 'export.by' must be full, day, month, or key; got '{value}'."),
        };

    /// <summary>The validated <c>target:</c> block.</summary>
    private sealed record MappedTarget(
        string Path,
        string? FileName,
        string FileType,
        string? Encoding,
        string Compression,
        string Delimiter,
        string TextQualifier,
        bool AddTimestamp,
        string? SubfolderPattern,
        bool Zip,
        ExportValueFormat ValueFormat);

    private static MappedTarget MapTarget(ExportTargetYaml y, string source)
    {
        var path = YamlDocumentParts.NullIfBlank(y.Path)
            ?? throw new FlowValidationException(
                $"{source}: 'target.path' is required (the folder for the exported files; a local path or file:// URI).");

        var fileType = y.FileType?.Trim().ToLowerInvariant() switch
        {
            null or "" or "csv" => "csv",
            "parquet" or "prq" => "parquet",
            _ => throw new FlowValidationException(
                $"{source}: 'target.fileType' must be csv or parquet; got '{y.FileType}'."),
        };

        // The writer recognizes exactly these tokens and quietly falls back to UTF-8 for anything else, so an
        // unrecognized token is rejected here instead of silently exporting in the wrong encoding.
        var encoding = y.Encoding?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "utf8" or "utf-8" => null,
            // The legacy engine's cloud path always wrote a UTF-8 byte-order mark, so a ported export that must
            // keep its consumer parsing unchanged asks for it by name.
            "utf8bom" or "utf-8bom" => "UTF8BOM",
            "unicode" or "utf16" or "utf-16" => "UTF-16",
            "utf32" or "utf-32" => "UTF-32",
            "ascii" => "ASCII",
            _ => throw new FlowValidationException(
                $"{source}: 'target.encoding' must be utf8, utf8bom, utf16, utf32, or ascii; got '{y.Encoding}'."),
        };

        // Same reasoning for the Parquet codec: the writer maps unknown values to gzip, so reject them here.
        var compression = y.Compression?.Trim().ToLowerInvariant() switch
        {
            null or "" or "gzip" => "gzip",
            "snappy" => "snappy",
            "none" => "none",
            _ => throw new FlowValidationException(
                $"{source}: 'target.compression' must be gzip, snappy, or none; got '{y.Compression}'."),
        };

        var delimiter = y.Delimiter ?? ";";
        if (delimiter.Length == 0)
        {
            throw new FlowValidationException($"{source}: 'target.delimiter' must not be empty.");
        }

        // The CSV writer quotes with exactly one character, so anything else would be silently truncated.
        var textQualifier = y.TextQualifier ?? "\"";
        if (textQualifier.Length != 1)
        {
            throw new FlowValidationException(
                $"{source}: 'target.textQualifier' must be a single character (omit it for the default double quote); got '{textQualifier}'.");
        }

        var valueFormat = y.ValueFormat?.Trim().ToLowerInvariant() switch
        {
            null or "" or "iso" => ExportValueFormat.Iso,
            "legacy" => ExportValueFormat.Legacy,
            _ => throw new FlowValidationException(
                $"{source}: 'target.valueFormat' must be iso or legacy; got '{y.ValueFormat}'."),
        };

        return new MappedTarget(path.Trim(), YamlDocumentParts.NullIfBlank(y.FileName)?.Trim(), fileType, encoding,
            compression, delimiter, textQualifier, y.AddTimestamp ?? true,
            YamlDocumentParts.NullIfBlank(y.SubfolderPattern)?.Trim(), y.Zip ?? false, valueFormat);
    }

    /// <summary>Normalizes the source filter into the form the segment planner appends verbatim after
    /// <c>WHERE 1=1</c>: a leading <c>" AND "</c> plus the predicate. The author may write the AND or not.</summary>
    private static string? NormalizeFilter(string? filter, string source)
    {
        var trimmed = filter?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var isAndKeyword = trimmed.StartsWith("and", StringComparison.OrdinalIgnoreCase)
            && (trimmed.Length == 3 || (!char.IsAsciiLetterOrDigit(trimmed[3]) && trimmed[3] != '_'));
        var body = isAndKeyword ? trimmed[3..].TrimStart() : trimmed;

        if (body.Length == 0)
        {
            throw new FlowValidationException($"{source}: 'source.filter' has no predicate after the AND.");
        }

        return " AND " + body;
    }
}
