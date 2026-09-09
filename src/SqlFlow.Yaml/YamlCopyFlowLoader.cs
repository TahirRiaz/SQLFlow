using SqlFlow.Core;
using SqlFlow.Core.Copy;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Yaml;

/// <summary>
/// Loads a file-copy flow (flowType: cpy) from YAML into a validated <see cref="CopyFlow"/>. YamlDotNet handles the
/// grammar; this class maps the parsed document, normalizes the operation enum, and enforces the source/target
/// requirements. Secret material (storage keys, SAS, connection strings) is always a <c>${...}</c> reference, never
/// inline, matching every other flow kind.
/// </summary>
public sealed class YamlCopyFlowLoader
{
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public CopyFlow LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Pipeline file not found: '{path}'.");
        }

        return Parse(File.ReadAllText(path), path);
    }

    public CopyFlow Parse(string yaml, string source = "<inline>")
    {
        CopyDocumentYaml? dto;
        try
        {
            dto = _deserializer.Deserialize<CopyDocumentYaml>(yaml);
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

    private static CopyFlow Map(CopyDocumentYaml y, string source)
    {
        var name = YamlDocumentParts.RequireFlowName(y.Name, "a cpy flow", source);

        return new CopyFlow
        {
            Name = name,
            Batch = YamlDocumentParts.NullIfBlank(y.Batch),
            Operation = ParseEnum(y.Operation, CopyOperation.Copy, "operation", source),
            Steps = MapSteps(y, source),
            Options = MapOptions(y.Options),
            Outputs = FileOutputMapping.Map(y.Output, y.Outputs, "cpy", source),
        };
    }

    /// <summary>Builds the copy steps from either the singular top-level <c>source:</c>/<c>target:</c> (one step, the
    /// simple case) or the plural <c>items:</c> list (one step per entry). Exactly one form is allowed: declaring both,
    /// or neither, fails at parse rather than silently copying nothing or copying twice.</summary>
    private static IReadOnlyList<CopyStep> MapSteps(CopyDocumentYaml y, string source)
    {
        var hasSingle = y.Source is not null || y.Target is not null;
        var hasItems = y.Items is { Count: > 0 };

        if (hasSingle && hasItems)
        {
            throw new FlowValidationException(
                $"{source}: declare either a top-level 'source'/'target' or an 'items' list, not both.");
        }

        if (hasItems)
        {
            var steps = new List<CopyStep>(y.Items!.Count);
            for (var i = 0; i < y.Items!.Count; i++)
            {
                var item = y.Items[i] ?? throw new FlowValidationException($"{source}: 'items[{i}]' must be a map.");
                var itemSource = item.Source ?? throw new FlowValidationException($"{source}: 'items[{i}].source' is required.");
                var itemTarget = item.Target ?? throw new FlowValidationException($"{source}: 'items[{i}].target' is required.");
                steps.Add(new CopyStep
                {
                    Source = MapEndpoint(itemSource, $"items[{i}].source", source),
                    Target = MapEndpoint(itemTarget, $"items[{i}].target", source),
                });
            }

            return steps;
        }

        if (!hasSingle)
        {
            throw new FlowValidationException(
                $"{source}: a cpy flow needs a 'source'/'target' pair or an 'items' list.");
        }

        var sourceYaml = y.Source ?? throw new FlowValidationException($"{source}: 'source' is required.");
        var targetYaml = y.Target ?? throw new FlowValidationException($"{source}: 'target' is required.");
        return [new CopyStep
        {
            Source = MapEndpoint(sourceYaml, "source", source),
            Target = MapEndpoint(targetYaml, "target", source),
        }];
    }

    private static CopyEndpoint MapEndpoint(CopyEndpointYaml y, string field, string source)
        => new()
        {
            Location = Require(y.Location, $"{field}.location", source),
            Pattern = string.IsNullOrWhiteSpace(y.Pattern) ? "*" : y.Pattern!.Trim(),
            Recursive = y.Recursive ?? true,
            ModifiedWithinDays = y.ModifiedWithinDays ?? 0,
            ConnectionStringRef = YamlDocumentParts.NullIfBlank(y.ConnectionStringRef),
            SasTokenRef = YamlDocumentParts.NullIfBlank(y.SasTokenRef),
            AccountKeyRef = YamlDocumentParts.NullIfBlank(y.AccountKeyRef),
            AccessKeyRef = YamlDocumentParts.NullIfBlank(y.AccessKeyRef),
            SecretKeyRef = YamlDocumentParts.NullIfBlank(y.SecretKeyRef),
            Region = YamlDocumentParts.NullIfBlank(y.Region),
        };

    private static CopyOptions MapOptions(CopyOptionsYaml? y)
    {
        if (y is null)
        {
            return new CopyOptions();
        }

        return new CopyOptions
        {
            Overwrite = y.Overwrite ?? true,
            PreserveStructure = y.PreserveStructure ?? true,
            SkipUnchanged = y.SkipUnchanged ?? true,
            ZipName = YamlDocumentParts.NullIfBlank(y.ZipName),
        };
    }

    private static string Require(string? value, string field, string source)
        => string.IsNullOrWhiteSpace(value) ? throw new FlowValidationException($"{source}: '{field}' is required.") : value.Trim();

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback, string field, string source) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var normalized = value.Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        foreach (var candidate in Enum.GetNames<TEnum>())
        {
            if (candidate.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return Enum.Parse<TEnum>(candidate);
            }
        }

        throw new FlowValidationException(
            $"{source}: '{value}' is not a valid value for '{field}'. Allowed: {string.Join(", ", Enum.GetNames<TEnum>())}.");
    }
}

// ---------------------------------------------------------------------------------------------------------------
// Binding DTOs (mutable, nullable; YamlDotNet only).
// ---------------------------------------------------------------------------------------------------------------

internal sealed class CopyDocumentYaml
{
    public string? Name { get; set; }
    public string? Batch { get; set; }
    public string? Operation { get; set; }
    public CopyEndpointYaml? Source { get; set; }
    public CopyEndpointYaml? Target { get; set; }
    public CopyOptionsYaml? Options { get; set; }

    /// <summary>Several source-to-target copies in one pipeline: one entry per file set. The alternative to the single
    /// top-level source/target.</summary>
    public List<CopyItemYaml>? Items { get; set; }

    /// <summary>A single declared output (convenience for the one-folder case).</summary>
    public FileOutputYaml? Output { get; set; }

    /// <summary>Several declared outputs: one entry per distinct folder/pattern the copy produces.</summary>
    public List<FileOutputYaml>? Outputs { get; set; }
}

internal sealed class CopyItemYaml
{
    public CopyEndpointYaml? Source { get; set; }
    public CopyEndpointYaml? Target { get; set; }
}

internal sealed class CopyEndpointYaml
{
    public string? Location { get; set; }
    public string? Pattern { get; set; }
    public bool? Recursive { get; set; }
    public int? ModifiedWithinDays { get; set; }
    public string? ConnectionStringRef { get; set; }
    public string? SasTokenRef { get; set; }
    public string? AccountKeyRef { get; set; }
    public string? AccessKeyRef { get; set; }
    public string? SecretKeyRef { get; set; }
    public string? Region { get; set; }
}

internal sealed class CopyOptionsYaml
{
    public bool? Overwrite { get; set; }
    public bool? PreserveStructure { get; set; }
    public bool? SkipUnchanged { get; set; }
    public string? ZipName { get; set; }
}
