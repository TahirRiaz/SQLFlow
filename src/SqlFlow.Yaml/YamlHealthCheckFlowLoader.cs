using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.HealthChecks;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Yaml;

/// <summary>
/// A parsed health-check document (flowType: hc): the flow itself plus the document-local connection registry
/// it declares. The connections become an in-memory data-source store, so the flow runs through the exact
/// same resolver and runner as full mode, with no control database anywhere.
/// </summary>
public sealed record HealthCheckDocument
{
    public required HealthCheckFlow Flow { get; init; }

    /// <summary>The document's named connections (the <c>connections:</c> block plus any synthesized from a
    /// direct <c>connection:</c> on the target endpoint).</summary>
    public required IReadOnlyList<DataSource> Connections { get; init; }
}

/// <summary>
/// Loads a health-check flow (an AutoML anomaly check over one monitored table's per-date metric) from YAML.
/// YamlDotNet handles the grammar; this class is the mapping/validation layer that turns the parsed document
/// into a validated <see cref="HealthCheckDocument"/>.
/// </summary>
public sealed class YamlHealthCheckFlowLoader
{
    private const string TargetConnectionName = "target";

    // The unquoted-scalar option types the connections block's map form the same way the other relational
    // loaders do; every typed DTO property is unaffected by it.
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .WithAttemptingUnquotedStringTypeDeserialization()
        .Build();

    public HealthCheckDocument LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Pipeline file not found: '{path}'.");
        }

        return Parse(File.ReadAllText(path), path);
    }

    public HealthCheckDocument Parse(string yaml, string source = "<inline>")
    {
        HealthCheckYaml? dto;
        try
        {
            dto = _deserializer.Deserialize<HealthCheckYaml>(yaml);
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

    private static HealthCheckDocument Map(HealthCheckYaml y, string source)
    {
        var name = YamlDocumentParts.RequireFlowName(y.Name, "a health-check flow", source);
        var connections = YamlDocumentParts.MapConnections(y.Connections, source);

        var targetYaml = y.Target ?? throw new FlowValidationException($"{source}: 'target' is required.");

        var server = YamlDocumentParts.ResolveEndpointConnection(
            targetYaml.Server, targetYaml.Connection, targetYaml.Provider,
            "target", TargetConnectionName, connections, source);

        // The series query is T-SQL (CAST AS float, GROUP BY) on the resolved server, so it is SQL Server by
        // design; a foreign provider is a configuration error caught at parse time, not deep in the run.
        YamlDocumentParts.RequireSqlServerConnection(connections, server, "target", "a health-check flow's target", source);

        var rawObject = YamlDocumentParts.NullIfBlank(targetYaml.Object)
            ?? throw new FlowValidationException(
                $"{source}: 'target.object' is required (a three-part name like Database.Schema.Table).");

        var dateColumn = YamlDocumentParts.NullIfBlank(y.DateColumn)?.Trim()
            ?? throw new FlowValidationException(
                $"{source}: 'dateColumn' is required (the date column the metrics are grouped by).");

        // The declaration body (metrics, ml ranges, maturity, holidays) validates through the shared parts, the
        // same vocabulary the embedded healthCheck: block of an ingestion flow uses.
        var ml = YamlHealthCheckParts.MapMl(y.Ml, source);
        var maturityDays = YamlHealthCheckParts.MapMaturityDays(y.MaturityDays, source);
        var sentinelFloor = YamlDocumentParts.ParseDate(y.SentinelDateFloor, "sentinelDateFloor", source) ?? new DateOnly(1990, 1, 1);

        var flow = new HealthCheckFlow
        {
            FlowId = YamlDocumentParts.StableFlowId(name),
            SysAlias = name,
            Batch = YamlDocumentParts.NullIfBlank(y.Batch),
            Lifecycle = YamlDocumentParts.ParseLifecycle(y.Lifecycle, source),
            Server = server,
            Target = YamlDocumentParts.ParseQualifiedObject(rawObject, "target.object", source),
            DateColumn = dateColumn,
            Metrics = YamlHealthCheckParts.MapMetrics(y.BaseValue, y.Metrics, source),
            FilterCriteria = YamlDocumentParts.NullIfBlank(y.Filter)?.Trim(),
            MaxExperimentSeconds = ml.MaxExperimentSeconds,
            AnomalyThreshold = ml.AnomalyThreshold,
            EsdAlpha = ml.EsdAlpha,
            MaxAnomalyFraction = ml.MaxAnomalyFraction,
            MaturityDays = maturityDays,
            SentinelDateFloor = sentinelFloor,
            Mode = YamlDocumentParts.ParseExecutionMode(y.Mode, "mode", source),
            Training = ml.Training,
            RetrainAfterDays = ml.RetrainAfterDays,
            Holidays = YamlHealthCheckParts.MapHolidays(y.Holidays, source),
            Description = YamlDocumentParts.NullIfBlank(y.Description),
        };

        return new HealthCheckDocument
        {
            Flow = flow,
            Connections = connections.Values.ToList(),
        };
    }
}
