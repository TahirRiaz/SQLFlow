namespace SqlFlow.Yaml;

// Mutable DTOs used only for YamlDotNet binding. They are mapped to the immutable
// SqlFlow.Core.Model records (with validation) by YamlFlowLoader.

internal sealed class FlowYaml
{
    public string? Name { get; set; }
    public string? Batch { get; set; }
    public string? Lifecycle { get; set; }
    public SourceYaml? Source { get; set; }
    public TargetYaml? Target { get; set; }
    public SchemaYaml? Schema { get; set; }
    public LoadYaml? Load { get; set; }
    public TransformYaml? Transform { get; set; }
    public List<string>? PreProcess { get; set; }
    public List<string>? PostProcess { get; set; }
    public string? DesiredIndexes { get; set; }
    public IncrementalYaml? Incremental { get; set; }
}

internal sealed class IncrementalYaml
{
    public string? Table { get; set; }
    public string? DateColumn { get; set; }
    public int? OverlapDays { get; set; }
    public string? WatermarkColumn { get; set; }
    public double? WatermarkOverlap { get; set; }
    public bool? FullLoad { get; set; }
}

internal sealed class TransformYaml
{
    public bool? InferTypes { get; set; }
    public string? OnConvertError { get; set; }
    public double? Threshold { get; set; }
    public int? Sample { get; set; }
    public bool? PreserveLeadingZeros { get; set; }
    public bool? GenerateView { get; set; }
    public List<TransformColumnYaml>? Columns { get; set; }
}

internal sealed class TransformColumnYaml
{
    public string? Name { get; set; }
    public string? Expr { get; set; }
    public string? As { get; set; }
    public string? Type { get; set; }
    public int? Order { get; set; }
    public bool? Virtual { get; set; }
    public bool? ExcludeFromView { get; set; }
}

internal sealed class SourceYaml
{
    public string? Type { get; set; }
    public string? Location { get; set; }
    public Dictionary<string, string?>? Options { get; set; }
}

internal sealed class TargetYaml
{
    public string? Connection { get; set; }
    public string? Schema { get; set; }
    public string? Table { get; set; }
}

internal sealed class SchemaYaml
{
    public string? Evolve { get; set; }
    public string? DefaultColumnType { get; set; }
    public Dictionary<string, OverrideYaml>? Overrides { get; set; }
}

internal sealed class OverrideYaml
{
    public string? Type { get; set; }
    public bool? Nullable { get; set; }
}

internal sealed class LoadYaml
{
    public string? Mode { get; set; }
    public int? BatchSize { get; set; }
    public bool? TableLock { get; set; }
    public bool? ManageIndexes { get; set; }
    public bool? ResetWhenConsolidated { get; set; }
}
