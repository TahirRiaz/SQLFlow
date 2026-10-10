using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.SchemaRegistration;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Yaml;

/// <summary>
/// A parsed schema registration document (flowType: sch): the flow itself plus the document-local connection
/// registry it declares, so the database resolves through the same secretless pipeline as every other flow.
/// </summary>
public sealed record SchemaRegistrationDocument
{
    public required SchemaRegistrationFlow Flow { get; init; }

    /// <summary>The document's named connections (the <c>connections:</c> block plus any synthesized from a
    /// direct <c>connection:</c> on <c>source</c>).</summary>
    public required IReadOnlyList<DataSource> Connections { get; init; }
}

/// <summary>
/// Loads a schema registration flow (flowType: sch) from YAML: <c>source</c> names the SQL Server database whose
/// tables and views are registered, and the optional <c>objects</c> block narrows it to some schemas.
/// </summary>
public sealed class YamlSchemaRegistrationFlowLoader
{
    private const string SourceConnectionName = "source";

    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public SchemaRegistrationDocument LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Pipeline file not found: '{path}'.");
        }

        return Parse(File.ReadAllText(path), path);
    }

    public SchemaRegistrationDocument Parse(string yaml, string source = "<inline>")
    {
        SchemaRegistrationYaml? dto;
        try
        {
            dto = _deserializer.Deserialize<SchemaRegistrationYaml>(yaml);
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

    private static SchemaRegistrationDocument Map(SchemaRegistrationYaml y, string source)
    {
        var name = YamlDocumentParts.RequireFlowName(y.Name, "a schema registration flow (flowType: sch)", source);

        var connections = YamlDocumentParts.MapConnections(y.Connections, source);

        var sourceYaml = y.Source ?? throw new FlowValidationException(
            $"{source}: 'source' is required (the connection of the database whose tables and views are registered).");
        var server = YamlDocumentParts.ResolveEndpointConnection(
            sourceYaml.Server, sourceYaml.Connection, sourceYaml.Provider, "source", SourceConnectionName, connections, source);

        // Registration reads SQL Server's catalog views; a foreign-provider source is a configuration error.
        YamlDocumentParts.RequireSqlServerConnection(connections, server, "source", "a schema registration flow's source", source);

        var include = y.Objects?.IncludeSchemas is { } includeList ? YamlDocumentParts.NormalizeSchemas(includeList) : [];
        var exclude = y.Objects?.ExcludeSchemas is { } excludeList ? YamlDocumentParts.NormalizeSchemas(excludeList) : [];
        var excludedIncluded = include.Where(s => exclude.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
        if (excludedIncluded.Count > 0)
        {
            throw new FlowValidationException(
                $"{source}: 'objects.includeSchemas' and 'objects.excludeSchemas' both name "
                + $"{string.Join(", ", excludedIncluded)}; a schema cannot be both registered and skipped.");
        }

        var flow = new SchemaRegistrationFlow
        {
            FlowId = YamlDocumentParts.StableFlowId(name),
            SysAlias = name,
            Description = YamlDocumentParts.NullIfBlank(y.Description),
            Batch = YamlDocumentParts.NullIfBlank(y.Batch),
            Lifecycle = YamlDocumentParts.ParseLifecycle(y.Lifecycle, source),
            Server = server,
            Scope = new SchemaRegistrationScope
            {
                Database = YamlDocumentParts.NullIfBlank(sourceYaml.Database)?.Trim(),
                IncludeSchemas = include,
                ExcludeSchemas = exclude,
            },
        };

        return new SchemaRegistrationDocument
        {
            Flow = flow,
            Connections = connections.Values.ToList(),
        };
    }

    private sealed class SchemaRegistrationYaml
    {
        public string? FlowType { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Batch { get; set; }
        public string? Lifecycle { get; set; }
        public Dictionary<string, object>? Connections { get; set; }
        public SchemaRegistrationSourceYaml? Source { get; set; }
        public SchemaRegistrationObjectsYaml? Objects { get; set; }
    }

    private sealed class SchemaRegistrationSourceYaml
    {
        public string? Server { get; set; }
        public string? Connection { get; set; }
        public string? Provider { get; set; }
        public string? Database { get; set; }
    }

    private sealed class SchemaRegistrationObjectsYaml
    {
        public List<string>? IncludeSchemas { get; set; }
        public List<string>? ExcludeSchemas { get; set; }
    }
}
