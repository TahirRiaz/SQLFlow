using System.Globalization;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Translate;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Yaml;

/// <summary>
/// A parsed translate document (flowType: trl): the flow itself plus the document-local connection registry it
/// declares, exactly as an export document carries its connections.
/// </summary>
public sealed record TranslateDocument
{
    public required TranslateFlow Flow { get; init; }

    /// <summary>The document's named connections (the <c>connections:</c> block plus any synthesized from a
    /// direct <c>connection:</c> on the source).</summary>
    public required IReadOnlyList<DataSource> Connections { get; init; }
}

/// <summary>
/// Loads a translation flow (a SQL Server query result mapped through a declared JSON template into shaped
/// documents, written to a destination and optionally delivered to an API) from YAML. YamlDotNet handles the
/// grammar; this class maps and validates the envelope AND compiles the free-form <c>template:</c> object graph
/// into the immutable <see cref="TranslateNode"/> tree. The template dialect: a mapping without <c>$</c> keys is
/// an object, a sequence is a fixed array, a scalar with <c>{Column}</c> tokens is a templated string (a scalar
/// that IS one token passes the column's native value through), any other scalar is a constant, and the
/// <c>$</c>-prefixed directives (<c>$column</c>, <c>$value</c>, <c>$template</c>, <c>$type</c>, <c>$format</c>,
/// <c>$whenNull</c>, <c>$default</c>, <c>$forEach</c>/<c>$item</c>) cover everything a bare scalar cannot say.
/// </summary>
public sealed class YamlTranslateFlowLoader
{
    private const string SourceConnectionName = "source";

    /// <summary>The reserved dataset name addressing the primary query's rows in a result-set-grain document.</summary>
    private const string PrimaryRowsDataset = "rows";

    // The unquoted-scalar option types the template's constants (a plain 42 stays a number, true a boolean,
    // a quoted '42' a string); every typed DTO property is unaffected by it.
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .WithAttemptingUnquotedStringTypeDeserialization()
        .Build();

    public TranslateDocument LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Pipeline file not found: '{path}'.");
        }

        return Parse(File.ReadAllText(path), path);
    }

    public TranslateDocument Parse(string yaml, string source = "<inline>")
    {
        TranslateYaml? dto;
        try
        {
            dto = _deserializer.Deserialize<TranslateYaml>(yaml);
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

    private static TranslateDocument Map(TranslateYaml y, string source)
    {
        var name = YamlDocumentParts.RequireFlowName(y.Name, "a translate flow", source);
        var connections = YamlDocumentParts.MapConnections(y.Connections, source);

        var sourceYaml = y.Source ?? throw new FlowValidationException($"{source}: 'source' is required.");
        var sourceServer = YamlDocumentParts.ResolveEndpointConnection(
            sourceYaml.Server, sourceYaml.Connection, sourceYaml.Provider,
            "source", SourceConnectionName, connections, source);

        // The translation read is T-SQL against the declared query, so the source is SQL Server by design; a
        // foreign source is a configuration error caught at parse time, not deep in the run.
        YamlDocumentParts.RequireSqlServerConnection(connections, sourceServer, "source", "a translate flow's source", source);

        var query = YamlDocumentParts.NullIfBlank(sourceYaml.Query)
            ?? throw new FlowValidationException($"{source}: 'source.query' is required (the SELECT whose rows become documents).");

        var datasets = MapDatasets(y.Datasets, source);
        var datasetNames = new HashSet<string>(datasets.Select(d => d.Name), StringComparer.OrdinalIgnoreCase);

        var grain = ParseEnum(y.Documents?.Per, TranslateDocumentGrain.Row, "documents.per", source);
        var nulls = ParseEnum(y.Documents?.Nulls, TranslateDocumentNulls.Omit, "documents.nulls", source);

        if (y.Template is null)
        {
            throw new FlowValidationException($"{source}: 'template' is required (the declared JSON shape).");
        }

        var template = CompileNode(y.Template, "template", datasetNames, grain, source);
        var output = MapOutput(y.Output, grain, source);
        var invoke = MapInvoke(y.Invoke, output, grain, source);

        var flow = new TranslateFlow
        {
            FlowId = YamlDocumentParts.StableFlowId(name),
            SysAlias = name,
            Batch = YamlDocumentParts.NullIfBlank(y.Batch),
            Lifecycle = YamlDocumentParts.ParseLifecycle(y.Lifecycle, source),
            SrcServer = sourceServer,
            Query = query.Trim(),
            Datasets = datasets,
            DocumentsPer = grain,
            Nulls = nulls,
            Template = template,
            Output = output,
            Invoke = invoke,
        };

        return new TranslateDocument { Flow = flow, Connections = connections.Values.ToList() };
    }

    private static IReadOnlyList<TranslateDataset> MapDatasets(List<TranslateDatasetYaml>? list, string source)
    {
        if (list is null || list.Count == 0)
        {
            return [];
        }

        var datasets = new List<TranslateDataset>(list.Count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < list.Count; i++)
        {
            var y = list[i] ?? throw new FlowValidationException($"{source}: 'datasets[{i}]' must be a map.");
            var name = YamlDocumentParts.NullIfBlank(y.Name)?.Trim()
                ?? throw new FlowValidationException($"{source}: 'datasets[{i}].name' is required.");
            if (string.Equals(name, PrimaryRowsDataset, StringComparison.OrdinalIgnoreCase))
            {
                throw new FlowValidationException(
                    $"{source}: 'datasets[{i}].name' cannot be '{PrimaryRowsDataset}'; that name is reserved for the primary query's rows.");
            }

            if (!names.Add(name))
            {
                throw new FlowValidationException($"{source}: dataset '{name}' is declared more than once.");
            }

            var query = YamlDocumentParts.NullIfBlank(y.Query)
                ?? throw new FlowValidationException($"{source}: 'datasets[{i}].query' is required.");

            // An empty (or absent) bind is a deliberate shape, not an error: a single-instance dataset that
            // resolves to all of its rows at any scope (a $row header block, or a global $forEach repeater).
            var bind = (y.Bind ?? [])
                .Select(b => b?.Trim())
                .Where(b => !string.IsNullOrEmpty(b))
                .Select(b => b!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            datasets.Add(new TranslateDataset { Name = name, Query = query.Trim(), Bind = bind });
        }

        return datasets;
    }

    // -----------------------------------------------------------------------------------------------------------
    // Template compilation
    // -----------------------------------------------------------------------------------------------------------

    private static readonly string[] LeafDirectives = ["$column", "$value", "$template"];
    private static readonly string[] KnownDirectives =
        ["$column", "$value", "$template", "$type", "$format", "$whenNull", "$default", "$forEach", "$row", "$item"];

    private static TranslateNode CompileNode(
        object? node, string path, IReadOnlySet<string> datasetNames, TranslateDocumentGrain grain, string source)
    {
        switch (node)
        {
            case null:
                return new TranslateValueNode { Source = TranslateValueSource.Constant, ConstantJson = "null" };

            case string text:
                return CompileScalarString(text, path, source);

            case IDictionary<object, object?> map:
                return CompileMapping(map, path, datasetNames, grain, source);

            case System.Collections.IEnumerable sequence:
            {
                var items = new List<TranslateNode>();
                var index = 0;
                foreach (var item in sequence)
                {
                    items.Add(CompileNode(item, $"{path}[{index}]", datasetNames, grain, source));
                    index++;
                }

                return new TranslateListNode(items);
            }

            default:
                // A typed scalar (bool/int/double/...) from the unquoted-scalar option: a constant.
                return new TranslateValueNode
                {
                    Source = TranslateValueSource.Constant,
                    ConstantJson = ToConstantJson(node, path, source),
                };
        }
    }

    private static TranslateNode CompileScalarString(string text, string path, string source)
    {
        try
        {
            if (TranslateTemplateText.IsSingleToken(text, out var column))
            {
                // "{Col}" and nothing else: the typed-passthrough form (a number stays a number).
                return new TranslateValueNode { Source = TranslateValueSource.Column, Column = column };
            }

            if (TranslateTemplateText.HasTokens(text))
            {
                return new TranslateValueNode { Source = TranslateValueSource.Template, Template = text };
            }
        }
        catch (SqlFlowException ex)
        {
            throw new FlowValidationException($"{source}: '{path}': {ex.Message}", ex);
        }

        return new TranslateValueNode { Source = TranslateValueSource.Constant, ConstantJson = ToConstantJson(text, path, source) };
    }

    private static TranslateNode CompileMapping(
        IDictionary<object, object?> map, string path, IReadOnlySet<string> datasetNames, TranslateDocumentGrain grain, string source)
    {
        var directives = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var plain = new List<(string Name, object? Value)>();
        foreach (var (rawKey, value) in map)
        {
            var key = Convert.ToString(rawKey, CultureInfo.InvariantCulture) ?? string.Empty;
            if (key.StartsWith('$'))
            {
                if (!KnownDirectives.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    throw new FlowValidationException(
                        $"{source}: '{path}' uses unknown directive '{key}'. Known: {string.Join(", ", KnownDirectives)}. " +
                        "To emit a literal property whose name starts with '$', wrap the object in '$value:'.");
                }

                if (!directives.TryAdd(key, value))
                {
                    throw new FlowValidationException($"{source}: '{path}' repeats directive '{key}'.");
                }
            }
            else
            {
                plain.Add((key, value));
            }
        }

        if (directives.Count == 0)
        {
            var properties = new List<TranslateProperty>(plain.Count);
            foreach (var (propertyName, value) in plain)
            {
                properties.Add(new TranslateProperty(
                    propertyName, CompileNode(value, $"{path}.{propertyName}", datasetNames, grain, source)));
            }

            return new TranslateObjectNode(properties);
        }

        if (plain.Count > 0)
        {
            throw new FlowValidationException(
                $"{source}: '{path}' mixes '$' directives with plain property names ({string.Join(", ", plain.Select(p => p.Name))}). " +
                "A node is either an object of plain properties or a single directive node.");
        }

        var hasForEach = directives.ContainsKey("$forEach");
        var hasRow = directives.ContainsKey("$row");
        if (hasForEach && hasRow)
        {
            throw new FlowValidationException(
                $"{source}: '{path}' declares both '$forEach' and '$row'; a node is a repeater (many rows) or a " +
                "single-row block, never both.");
        }

        return hasForEach || hasRow
            ? CompileDatasetNode(directives, hasRow ? "$row" : "$forEach", path, datasetNames, grain, source)
            : CompileLeaf(directives, path, source);
    }

    /// <summary>Compiles the two dataset-driven shapes, which share one grammar: the directive names the dataset,
    /// <c>$item</c> is the body. <c>$forEach</c> emits one array element per matching row (the repeater);
    /// <c>$row</c> renders its body once in the scope of the single matching row (the header / one-to-one block).</summary>
    private static TranslateNode CompileDatasetNode(
        Dictionary<string, object?> directives, string directive, string path, IReadOnlySet<string> datasetNames,
        TranslateDocumentGrain grain, string source)
    {
        foreach (var key in directives.Keys)
        {
            if (!key.Equals(directive, StringComparison.OrdinalIgnoreCase) && !key.Equals("$item", StringComparison.OrdinalIgnoreCase))
            {
                throw new FlowValidationException(
                    $"{source}: '{path}' combines '{directive}' with '{key}'; a dataset node takes exactly '{directive}' and '$item'.");
            }
        }

        var dataset = (directives[directive] as string)?.Trim();
        if (string.IsNullOrEmpty(dataset))
        {
            throw new FlowValidationException($"{source}: '{path}.{directive}' must name a dataset (or '{PrimaryRowsDataset}').");
        }

        if (string.Equals(dataset, PrimaryRowsDataset, StringComparison.OrdinalIgnoreCase))
        {
            if (grain != TranslateDocumentGrain.ResultSet)
            {
                throw new FlowValidationException(
                    $"{source}: '{path}.{directive}: {PrimaryRowsDataset}' addresses the whole primary result, which only exists at " +
                    "'documents.per: resultSet'; a per-row document IS one primary row.");
            }
        }
        else if (!datasetNames.Contains(dataset))
        {
            throw new FlowValidationException(
                $"{source}: '{path}.{directive}' references dataset '{dataset}', which is not declared under 'datasets:'." +
                (datasetNames.Count == 0 ? string.Empty : $" Declared: {string.Join(", ", datasetNames)}."));
        }

        if (!directives.TryGetValue("$item", out var item) || item is null)
        {
            throw new FlowValidationException(
                $"{source}: '{path}' declares '{directive}' and therefore requires '$item' (the body template).");
        }

        var body = CompileNode(item, $"{path}.$item", datasetNames, grain, source);
        return directive == "$row"
            ? new TranslateRowNode(dataset!, body)
            : new TranslateArrayNode(dataset!, body);
    }

    private static TranslateNode CompileLeaf(Dictionary<string, object?> directives, string path, string source)
    {
        if (directives.TryGetValue("$item", out _))
        {
            throw new FlowValidationException($"{source}: '{path}' declares '$item' without '$forEach'.");
        }

        var sources = LeafDirectives.Where(directives.ContainsKey).ToList();
        if (sources.Count != 1)
        {
            throw new FlowValidationException(
                $"{source}: '{path}' must declare exactly one of $column, $value, or $template; found " +
                (sources.Count == 0 ? "none" : string.Join(" and ", sources)) + ".");
        }

        var type = ParseEnum(directives.GetValueOrDefault("$type") as string, TranslateValueType.Auto, $"{path}.$type", source);
        if (directives.ContainsKey("$type") && directives.GetValueOrDefault("$type") is not string)
        {
            throw new FlowValidationException($"{source}: '{path}.$type' must be a scalar type name.");
        }

        var format = YamlDocumentParts.NullIfBlank(directives.GetValueOrDefault("$format") as string);
        if (format is not null && type is not (TranslateValueType.Date or TranslateValueType.DateTime))
        {
            throw new FlowValidationException(
                $"{source}: '{path}.$format' applies only to '$type: date' or '$type: dateTime'; this leaf is '{type}'.");
        }

        var hasDefault = directives.TryGetValue("$default", out var defaultValue);
        var whenNullRaw = directives.GetValueOrDefault("$whenNull") as string;
        var whenNull = ParseEnum(whenNullRaw, TranslateNullPolicy.Inherit, $"{path}.$whenNull", source);
        if (whenNull == TranslateNullPolicy.Inherit && directives.ContainsKey("$whenNull"))
        {
            throw new FlowValidationException(
                $"{source}: '{path}.$whenNull' must be omit, null, or default (leave it out to follow 'documents.nulls').");
        }

        if (whenNull == TranslateNullPolicy.Default && !hasDefault)
        {
            throw new FlowValidationException($"{source}: '{path}' sets '$whenNull: default' without a '$default' value.");
        }

        if (hasDefault)
        {
            if (directives.ContainsKey("$whenNull") && whenNull != TranslateNullPolicy.Default)
            {
                throw new FlowValidationException(
                    $"{source}: '{path}' declares '$default' but '$whenNull: {whenNullRaw}'; a default is only emitted under '$whenNull: default'.");
            }

            whenNull = TranslateNullPolicy.Default;
        }

        if (directives.TryGetValue("$column", out var columnValue))
        {
            var column = (columnValue as string)?.Trim();
            if (string.IsNullOrEmpty(column))
            {
                throw new FlowValidationException($"{source}: '{path}.$column' must name a source column.");
            }

            return new TranslateValueNode
            {
                Source = TranslateValueSource.Column,
                Column = column,
                Type = type,
                Format = format,
                WhenNull = whenNull,
                DefaultJson = hasDefault ? ToConstantJson(defaultValue, $"{path}.$default", source) : null,
            };
        }

        if (directives.TryGetValue("$template", out var templateValue))
        {
            if (templateValue is not string template || string.IsNullOrEmpty(template))
            {
                throw new FlowValidationException($"{source}: '{path}.$template' must be a non-empty string.");
            }

            try
            {
                TranslateTemplateText.Parse(template);
            }
            catch (SqlFlowException ex)
            {
                throw new FlowValidationException($"{source}: '{path}.$template': {ex.Message}", ex);
            }

            return new TranslateValueNode
            {
                Source = TranslateValueSource.Template,
                Template = template,
                Type = type,
                Format = format,
                WhenNull = whenNull,
                DefaultJson = hasDefault ? ToConstantJson(defaultValue, $"{path}.$default", source) : null,
            };
        }

        // $value: a constant emitted verbatim, so coercion and null policies do not apply to it.
        if (directives.ContainsKey("$type") || directives.ContainsKey("$whenNull") || hasDefault)
        {
            throw new FlowValidationException(
                $"{source}: '{path}' combines '$value' with '$type'/'$whenNull'/'$default'; a constant is emitted verbatim.");
        }

        return new TranslateValueNode
        {
            Source = TranslateValueSource.Constant,
            ConstantJson = ToConstantJson(directives.GetValueOrDefault("$value"), $"{path}.$value", source),
        };
    }

    /// <summary>Serializes a YAML constant (scalar, mapping, or sequence) to canonical JSON text. Strings inside a
    /// constant are verbatim: no <c>{Column}</c> rendering happens under <c>$value</c>.</summary>
    private static string ToConstantJson(object? value, string path, string source)
        => ToConstantNode(value, path, source)?.ToJsonString() ?? "null";

    private static JsonNode? ToConstantNode(object? value, string path, string source)
    {
        switch (value)
        {
            case null:
                return null;
            case string s:
                return JsonValue.Create(s);
            case bool b:
                return JsonValue.Create(b);
            case byte or sbyte or short or ushort or int or uint or long:
                return JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            case ulong ul:
                return JsonValue.Create(ul);
            case float or double:
                return JsonValue.Create(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            case decimal m:
                return JsonValue.Create(m);
            case IDictionary<object, object?> map:
            {
                var obj = new JsonObject();
                foreach (var (rawKey, item) in map)
                {
                    var key = Convert.ToString(rawKey, CultureInfo.InvariantCulture) ?? string.Empty;
                    obj[key] = ToConstantNode(item, $"{path}.{key}", source);
                }

                return obj;
            }

            case System.Collections.IEnumerable sequence:
            {
                var array = new JsonArray();
                var index = 0;
                foreach (var item in sequence)
                {
                    array.Add(ToConstantNode(item, $"{path}[{index}]", source));
                    index++;
                }

                return array;
            }

            default:
                throw new FlowValidationException(
                    $"{source}: '{path}' holds an unsupported constant of type {value.GetType().Name}.");
        }
    }

    // -----------------------------------------------------------------------------------------------------------
    // Output and invoke
    // -----------------------------------------------------------------------------------------------------------

    private static TranslateOutput MapOutput(TranslateOutputYaml? y, TranslateDocumentGrain grain, string source)
    {
        if (y is null)
        {
            throw new FlowValidationException(
                $"{source}: 'output' is required. The saved files are the durable record of the translation; the " +
                "optional 'invoke' step delivers exactly what was saved.");
        }

        var path = YamlDocumentParts.NullIfBlank(y.Path)?.Trim()
            ?? throw new FlowValidationException(
                $"{source}: 'output.path' is required (the destination folder; a local path, file:// URI, or Azure storage URI).");

        var mode = ParseEnum(y.Mode, TranslateOutputMode.JsonLines, "output.mode", source);

        var fileName = YamlDocumentParts.NullIfBlank(y.FileName)?.Trim();
        if (fileName is not null)
        {
            IReadOnlyList<string> tokens;
            try
            {
                tokens = TranslateTemplateText.Columns(fileName);
            }
            catch (SqlFlowException ex)
            {
                throw new FlowValidationException($"{source}: 'output.fileName': {ex.Message}", ex);
            }

            if (tokens.Count > 0 && (mode != TranslateOutputMode.FilePerDocument || grain != TranslateDocumentGrain.Row))
            {
                throw new FlowValidationException(
                    $"{source}: 'output.fileName' uses {{Column}} tokens, which need a per-document row scope: " +
                    "'output.mode: filePerDocument' with 'documents.per: row'.");
            }
        }

        var indent = y.Indent ?? false;
        if (indent && mode == TranslateOutputMode.JsonLines)
        {
            throw new FlowValidationException(
                $"{source}: 'output.indent' does not apply to 'output.mode: jsonLines' (a JSON Lines document is one compact line).");
        }

        return new TranslateOutput
        {
            Path = path,
            Mode = mode,
            FileName = fileName,
            AddTimestamp = y.AddTimestamp ?? false,
            Indent = indent,
        };
    }

    private static TranslateInvoke? MapInvoke(
        TranslateInvokeYaml? y, TranslateOutput output, TranslateDocumentGrain grain, string source)
    {
        if (y is null)
        {
            return null;
        }

        var url = YamlDocumentParts.NullIfBlank(y.Url)?.Trim()
            ?? throw new FlowValidationException($"{source}: 'invoke.url' is required.");

        var batchSize = y.BatchSize ?? 1;
        if (batchSize < 1)
        {
            throw new FlowValidationException($"{source}: 'invoke.batchSize' must be at least 1, got {batchSize}.");
        }

        if (output.Mode == TranslateOutputMode.Array && batchSize != 1)
        {
            throw new FlowValidationException(
                $"{source}: 'invoke.batchSize' does not apply to 'output.mode: array'; the saved array posts as one request.");
        }

        IReadOnlyList<string> urlTokens;
        try
        {
            urlTokens = TranslateTemplateText.Columns(url);
        }
        catch (SqlFlowException ex)
        {
            throw new FlowValidationException($"{source}: 'invoke.url': {ex.Message}", ex);
        }

        if (urlTokens.Count > 0
            && (output.Mode != TranslateOutputMode.FilePerDocument || grain != TranslateDocumentGrain.Row || batchSize != 1))
        {
            throw new FlowValidationException(
                $"{source}: 'invoke.url' uses {{Column}} tokens, which need exactly one document (and its row) per request: " +
                "'output.mode: filePerDocument' with 'documents.per: row' and 'invoke.batchSize: 1'.");
        }

        return new TranslateInvoke
        {
            Url = url,
            Method = YamlDocumentParts.NullIfBlank(y.Method)?.Trim().ToUpperInvariant() ?? "POST",
            Headers = y.Headers ?? new Dictionary<string, string>(StringComparer.Ordinal),
            Auth = YamlAcquireFlowLoader.MapAuth(y.Auth, source, "invoke.auth"),
            BatchSize = batchSize,
            EnvelopeKey = YamlDocumentParts.NullIfBlank(y.EnvelopeKey)?.Trim(),
            // Delivery is in-order by default (concurrency 1); a flow opts into parallel requests explicitly.
            Reliability = YamlAcquireFlowLoader.MapReliability(y.Reliability, source, "invoke.reliability", defaultConcurrency: 1),
        };
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback, string field, string source) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var normalized = value.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        foreach (var name in Enum.GetNames<TEnum>())
        {
            if (name.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return Enum.Parse<TEnum>(name);
            }
        }

        throw new FlowValidationException(
            $"{source}: '{value}' is not a valid value for '{field}'. Allowed: {string.Join(", ", Enum.GetNames<TEnum>())}.");
    }
}
