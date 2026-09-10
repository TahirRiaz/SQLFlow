using SqlFlow.Core;
using SqlFlow.Core.Model;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Yaml;

/// <summary>
/// Loads a standalone inference job from its own YAML structure (distinct from a pipeline flow) into
/// an <see cref="InferenceRequest"/>.
/// </summary>
public sealed class InferSpecLoader
{
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public InferenceRequest LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Inference spec not found: '{path}'.");
        }

        return Parse(File.ReadAllText(path), path);
    }

    public InferenceRequest Parse(string yaml, string source = "<inline>")
    {
        InferYaml? dto;
        try
        {
            dto = _deserializer.Deserialize<InferYaml>(yaml);
        }
        catch (YamlException ex)
        {
            throw new FlowValidationException($"{source}: invalid YAML - {ex.Message}", ex);
        }

        if (dto is null)
        {
            throw new FlowValidationException($"{source}: the document is empty.");
        }

        var (schema, table) = SplitTable(Required(dto.Table, "table", source), dto.Schema);

        return new InferenceRequest
        {
            Connection = Required(dto.Connection, "connection", source),
            Schema = schema,
            Table = table,
            Policy = new TypeInferencePolicy
            {
                Enabled = true,
                OnConvertError = ParseErrorMode(dto.OnConvertError, source),
                Threshold = dto.Threshold ?? 1.0,
                SampleSize = dto.Sample ?? 0,
                PreserveLeadingZeros = dto.PreserveLeadingZeros ?? true,
                Culture = string.IsNullOrWhiteSpace(dto.Culture) ? null : dto.Culture.Trim(),
            },
        };
    }

    private static (string Schema, string Table) SplitTable(string table, string? schema)
    {
        var trimmed = table.Replace("[", string.Empty, StringComparison.Ordinal).Replace("]", string.Empty, StringComparison.Ordinal);
        var dot = trimmed.LastIndexOf('.');
        return dot > 0
            ? (trimmed[..dot], trimmed[(dot + 1)..])
            : (string.IsNullOrWhiteSpace(schema) ? "dbo" : schema, trimmed);
    }

    private static ConvertErrorMode ParseErrorMode(string? value, string source)
    {
        var normalized = value?.Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            // Fail-loud by default: a value that diverges from the profiled sample halts the load
            // rather than silently becoming NULL. Opt into leniency with onConvertError: silentNull.
            return ConvertErrorMode.Fail;
        }

        if (Enum.TryParse<ConvertErrorMode>(normalized, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw new FlowValidationException(
            $"{source}: 'onConvertError' has invalid value '{value}'. Allowed: {string.Join(", ", Enum.GetNames<ConvertErrorMode>())}.");
    }

    private static string Required(string? value, string field, string source)
        => !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new FlowValidationException($"{source}: '{field}' is required.");
}

internal sealed class InferYaml
{
    public string? Connection { get; set; }
    public string? Schema { get; set; }
    public string? Table { get; set; }
    public string? OnConvertError { get; set; }
    public double? Threshold { get; set; }
    public int? Sample { get; set; }
    public bool? PreserveLeadingZeros { get; set; }
    public string? Culture { get; set; }
}
