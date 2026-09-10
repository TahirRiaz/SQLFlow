using SqlFlow.Core;
using SqlFlow.Core.Engine;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Model;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Yaml;

/// <summary>
/// Loads a pipeline definition from YAML. YamlDotNet handles the grammar; this class is the
/// mapping/validation layer that turns the parsed document into a validated <see cref="FlowDefinition"/>.
/// </summary>
public sealed class YamlFlowLoader
{
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public FlowDefinition LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Pipeline file not found: '{path}'.");
        }

        return Parse(File.ReadAllText(path), path);
    }

    public FlowDefinition Parse(string yaml, string source = "<inline>")
    {
        FlowYaml? dto;
        try
        {
            dto = _deserializer.Deserialize<FlowYaml>(yaml);
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

    private static FlowDefinition Map(FlowYaml y, string source)
    {
        var src = y.Source ?? throw new FlowValidationException($"{source}: 'source' is required.");
        var tgt = y.Target ?? throw new FlowValidationException($"{source}: 'target' is required.");
        var name = Required(y.Name, "name", source);

        var options = src.Options is null
            ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string?>(src.Options, StringComparer.OrdinalIgnoreCase);

        // FlowId is system-generated (not authored in YAML); expose it to the metadata record too.
        options["flowId"] = FlowIdentity.FromName(name).ToString();

        return new FlowDefinition
        {
            Name = name,
            Batch = YamlDocumentParts.NullIfBlank(y.Batch),
            Lifecycle = YamlDocumentParts.ParseLifecycle(y.Lifecycle, source),
            Source = new SourceSpec
            {
                Type = Required(src.Type, "source.type", source),
                Location = src.Location,
                Options = options,
            },
            Target = new TargetSpec
            {
                Connection = Required(tgt.Connection, "target.connection", source),
                Schema = Required(tgt.Schema, "target.schema", source),
                Table = Required(tgt.Table, "target.table", source),
            },
            Schema = MapSchema(y.Schema, ResolveDefaultColumnType(y.Schema, src), source),
            Load = MapLoad(y.Load, source),
            Inference = MapInference(y.Transform, source),
            PreProcess = y.PreProcess ?? [],
            PostProcess = y.PostProcess ?? [],
            DesiredIndexes = string.IsNullOrWhiteSpace(y.DesiredIndexes) ? null : y.DesiredIndexes,
            Incremental = MapIncremental(y.Incremental),
        };
    }

    private static IncrementalSpec? MapIncremental(IncrementalYaml? y)
    {
        if (y is null)
        {
            return null;
        }

        var watermarkColumn = string.IsNullOrWhiteSpace(y.WatermarkColumn) ? null : y.WatermarkColumn;

        // The two modes are mutually exclusive. Row-level mode ignores dateColumn/overlapDays (which bound files,
        // not rows), so reject those file-date knobs together with a watermarkColumn to fail loud, not silently.
        if (watermarkColumn is not null)
        {
            if (y.OverlapDays is not null)
            {
                throw new SqlFlowException(
                    "incremental: 'overlapDays' applies to the file-date watermark; with a row-level 'watermarkColumn' use 'watermarkOverlap'.");
            }

            if (!string.IsNullOrWhiteSpace(y.DateColumn))
            {
                throw new SqlFlowException(
                    "incremental: 'dateColumn' is the file-date watermark; it cannot be combined with a row-level 'watermarkColumn'.");
            }
        }

        if (y.WatermarkOverlap is not null)
        {
            if (watermarkColumn is null)
            {
                throw new SqlFlowException("incremental: 'watermarkOverlap' requires a 'watermarkColumn'.");
            }

            if (y.WatermarkOverlap < 0)
            {
                throw new SqlFlowException($"incremental: 'watermarkOverlap' must be zero or positive, was {y.WatermarkOverlap}.");
            }
        }

        return new IncrementalSpec
        {
            Table = string.IsNullOrWhiteSpace(y.Table) ? null : y.Table,
            DateColumn = string.IsNullOrWhiteSpace(y.DateColumn) ? "FileDate_DW" : y.DateColumn,
            OverlapDays = y.OverlapDays ?? 0,
            WatermarkColumn = watermarkColumn,
            WatermarkOverlap = y.WatermarkOverlap ?? 0,
            FullLoad = y.FullLoad ?? false,
        };
    }

    /// <summary>Maps the shared <c>transform:</c> block (also used by the relational ingestion loader, so both
    /// flow kinds carry the same policy shape and validation).</summary>
    internal static TypeInferencePolicy MapInference(TransformYaml? y, string source)
    {
        if (y is null)
        {
            return new TypeInferencePolicy();
        }

        return new TypeInferencePolicy
        {
            Enabled = y.InferTypes ?? false,
            OnConvertError = ParseEnum(NormalizeToken(y.OnConvertError), ConvertErrorMode.SilentNull, "transform.onConvertError", source),
            Threshold = y.Threshold ?? 1.0,
            SampleSize = y.Sample ?? 0,
            PreserveLeadingZeros = y.PreserveLeadingZeros ?? true,
            GenerateView = y.GenerateView ?? true,
            Columns = MapTransformColumns(y.Columns, source),
        };
    }

    /// <summary>
    /// Maps the authored per-column transforms, validating each so a malformed transform section fails the load
    /// with a clear message rather than emitting a broken view at run time. Names must be present and unique
    /// (case-insensitive); a virtual column needs an expression and cannot use the <c>@ColName</c> token; a plain
    /// column needs either an expression or a type (otherwise it is a no-op the author almost certainly did not
    /// intend).
    /// </summary>
    private static IReadOnlyList<ColumnTransform> MapTransformColumns(List<TransformColumnYaml>? columns, string source)
    {
        if (columns is null || columns.Count == 0)
        {
            return [];
        }

        var mapped = new List<ColumnTransform>(columns.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columns.Count; i++)
        {
            var c = columns[i];
            var name = c.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new FlowValidationException($"{source}: transform.columns[{i}] is missing 'name'.");
            }

            if (!seen.Add(name))
            {
                throw new FlowValidationException($"{source}: transform.columns has a duplicate column '{name}'.");
            }

            var expression = string.IsNullOrWhiteSpace(c.Expr) ? null : c.Expr.Trim();
            var type = string.IsNullOrWhiteSpace(c.Type) ? null : c.Type.Trim();
            var alias = string.IsNullOrWhiteSpace(c.As) ? null : c.As.Trim();
            var isVirtual = c.Virtual ?? false;

            if (isVirtual)
            {
                if (expression is null)
                {
                    throw new FlowValidationException($"{source}: transform.columns['{name}'] is virtual and must declare an 'expr'.");
                }

                if (ColumnTransformExpression.ReferencesColumnToken(expression))
                {
                    throw new FlowValidationException(
                        $"{source}: transform.columns['{name}'] is virtual, so its 'expr' cannot use @ColName (there is no source column); reference other columns by name.");
                }
            }
            else if (expression is null && type is null && !(c.ExcludeFromView ?? false))
            {
                throw new FlowValidationException(
                    $"{source}: transform.columns['{name}'] does nothing - declare an 'expr', a 'type' (to cast), mark it 'virtual', or set 'excludeFromView' to drop it.");
            }

            mapped.Add(new ColumnTransform
            {
                Name = name,
                Expression = expression,
                Alias = alias,
                Type = type,
                SortOrder = c.Order,
                Virtual = isVirtual,
                ExcludeFromView = c.ExcludeFromView ?? false,
            });
        }

        return mapped;
    }

    private static string ResolveDefaultColumnType(SchemaYaml? schema, SourceYaml src)
    {
        if (!string.IsNullOrWhiteSpace(schema?.DefaultColumnType))
        {
            return schema.DefaultColumnType;
        }

        // Honor the source metadata's DefaultColDataType (SQLFlow convention) before the global default.
        if (src.Options is not null
            && src.Options.TryGetValue("defaultColDataType", out var fromMetadata)
            && !string.IsNullOrWhiteSpace(fromMetadata))
        {
            return fromMetadata;
        }

        return "varchar(255)";
    }

    private static SchemaPolicy MapSchema(SchemaYaml? y, string defaultColumnType, string source)
    {
        var overrides = new Dictionary<string, ColumnOverride>(StringComparer.OrdinalIgnoreCase);
        if (y?.Overrides is not null)
        {
            foreach (var (key, value) in y.Overrides)
            {
                overrides[key] = new ColumnOverride { Type = value?.Type, Nullable = value?.Nullable };
            }
        }

        return new SchemaPolicy
        {
            Evolve = ParseEnum(y?.Evolve, SchemaEvolution.Widen, "schema.evolve", source),
            DefaultColumnType = defaultColumnType,
            Overrides = overrides,
        };
    }

    private static LoadPolicy MapLoad(LoadYaml? y, string source)
    {
        if (y is null)
        {
            return new LoadPolicy();
        }

        return new LoadPolicy
        {
            Mode = ParseEnum(NormalizeToken(y.Mode), LoadMode.Append, "load.mode", source),
            BatchSize = y.BatchSize ?? 50_000,
            TableLock = y.TableLock ?? true,
            ManageIndexes = y.ManageIndexes ?? false,
            ResetWhenConsolidated = y.ResetWhenConsolidated ?? true,
        };
    }

    private static string? NormalizeToken(string? value)
        => value?.Replace("-", string.Empty, StringComparison.Ordinal)
                 .Replace("_", string.Empty, StringComparison.Ordinal);

    private static string Required(string? value, string field, string source)
        => !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new FlowValidationException($"{source}: '{field}' is required.");

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback, string field, string source)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw new FlowValidationException(
            $"{source}: '{field}' has invalid value '{value}'. Allowed: {string.Join(", ", Enum.GetNames<TEnum>())}.");
    }
}
