using SqlFlow.Core;
using SqlFlow.Core.Batch;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Yaml;

/// <summary>A parsed batch document (flowType: batch): the batch flow and nothing else (a batch owns no
/// connections; its members carry their own).</summary>
public sealed record BatchDocument
{
    public required BatchFlow Flow { get; init; }
}

/// <summary>
/// Loads a batch flow (flowType: batch) from YAML and validates it into a <see cref="BatchDocument"/>. A batch
/// names its members by include/exclude globs, chooses an error mode (stop or continue), can mark members
/// inactive or their errors ignorable, and tunes concurrency and the lineage connect mode.
/// </summary>
public sealed class YamlBatchFlowLoader
{
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public BatchDocument LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Pipeline file not found: '{path}'.");
        }

        return Parse(File.ReadAllText(path), path);
    }

    public BatchDocument Parse(string yaml, string source = "<inline>")
    {
        BatchYaml? dto;
        try
        {
            dto = _deserializer.Deserialize<BatchYaml>(yaml);
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

    private static BatchDocument Map(BatchYaml y, string source)
    {
        var name = YamlDocumentParts.RequireFlowName(y.Name, "a batch flow (flowType: batch)", source);

        var include = Clean(y.Members?.Include);
        if (include.Count == 0)
        {
            throw new FlowValidationException(
                $"{source}: a batch needs at least one 'members.include' glob (relative to the batch file's directory).");
        }

        var maxParallel = y.MaxParallel ?? 0;
        if (maxParallel < 0)
        {
            throw new FlowValidationException($"{source}: 'maxParallel' must be 0 (unbounded) or a positive number, got {maxParallel}.");
        }

        var flow = new BatchFlow
        {
            FlowId = YamlDocumentParts.StableFlowId(name),
            SysAlias = name,
            Description = YamlDocumentParts.NullIfBlank(y.Description),
            Lifecycle = YamlDocumentParts.ParseLifecycle(y.Lifecycle, source),
            Include = include,
            Exclude = Clean(y.Members?.Exclude),
            Inactive = Clean(y.Members?.Inactive),
            IgnoreErrors = Clean(y.IgnoreErrors),
            OnError = ParseErrorMode(y.OnError, source),
            MaxParallel = maxParallel,
            Connect = ParseConnect(y.Connect, source),
        };

        return new BatchDocument { Flow = flow };
    }

    private static BatchErrorMode ParseErrorMode(string? value, string source)
        => YamlDocumentParts.NullIfBlank(value)?.Trim().ToLowerInvariant() switch
        {
            null or "stop" => BatchErrorMode.Stop,
            "continue" => BatchErrorMode.Continue,
            _ => throw new FlowValidationException($"{source}: 'onError' must be 'stop' or 'continue', got '{value}'."),
        };

    private static BatchConnectMode ParseConnect(string? value, string source)
        => YamlDocumentParts.NullIfBlank(value)?.Trim().ToLowerInvariant() switch
        {
            null or "auto" => BatchConnectMode.Auto,
            "always" => BatchConnectMode.Always,
            "never" => BatchConnectMode.Never,
            _ => throw new FlowValidationException($"{source}: 'connect' must be 'auto', 'always', or 'never', got '{value}'."),
        };

    private static IReadOnlyList<string> Clean(List<string>? items)
        => items is null
            ? []
            : items.Select(i => i?.Trim()).Where(i => !string.IsNullOrEmpty(i)).Select(i => i!).ToList();

    private sealed class BatchYaml
    {
        public string? FlowType { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Lifecycle { get; set; }
        public BatchMembersYaml? Members { get; set; }
        public string? OnError { get; set; }
        public List<string>? IgnoreErrors { get; set; }
        public int? MaxParallel { get; set; }
        public string? Connect { get; set; }
    }

    private sealed class BatchMembersYaml
    {
        public List<string>? Include { get; set; }
        public List<string>? Exclude { get; set; }
        public List<string>? Inactive { get; set; }
    }
}
