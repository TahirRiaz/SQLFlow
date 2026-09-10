using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.StoredProcedures;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Yaml;

/// <summary>
/// A parsed stored-procedure document (flowType: sp): the flow itself plus the document-local connection
/// registry it declares. The connections become an in-memory data-source store, so the flow runs through the
/// exact same resolver and runner as full mode, with no control database anywhere.
/// </summary>
public sealed record StoredProcedureDocument
{
    public required StoredProcedureFlow Flow { get; init; }

    /// <summary>The document's named connections (the <c>connections:</c> block plus any synthesized from a
    /// direct <c>connection:</c> on the procedure endpoint).</summary>
    public required IReadOnlyList<DataSource> Connections { get; init; }

    /// <summary>The document's named invokes (the <c>invokes:</c> block), referenced by postInvoke.</summary>
    public IReadOnlyList<Core.Invoke.InvokeDefinition> Invokes { get; init; } = [];

    /// <summary>The document's named Azure service principals (the <c>servicePrincipals:</c> block).</summary>
    public IReadOnlyList<ServicePrincipalProfile> ServicePrincipals { get; init; } = [];
}

/// <summary>
/// Loads a stored-procedure flow (one existing procedure executed on a resolved SQL Server) from YAML.
/// YamlDotNet handles the grammar; this class is the mapping/validation layer that turns the parsed document
/// into a validated <see cref="StoredProcedureDocument"/>. Input parameters are bound from the
/// <c>procedure.parameters</c> block, either as literals or as scalar queries resolved at run time, which is
/// the V3 form of the legacy flw.Parameter table.
/// </summary>
public sealed class YamlStoredProcedureFlowLoader
{
    private const string TargetConnectionName = "target";

    // The unquoted-scalar option types the invoke parameters block (a plain true/42 stays a boolean/number,
    // a quoted scalar a string); every typed DTO property is unaffected by it.
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .WithAttemptingUnquotedStringTypeDeserialization()
        .Build();

    public StoredProcedureDocument LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Pipeline file not found: '{path}'.");
        }

        return Parse(File.ReadAllText(path), path);
    }

    public StoredProcedureDocument Parse(string yaml, string source = "<inline>")
    {
        StoredProcedureYaml? dto;
        try
        {
            dto = _deserializer.Deserialize<StoredProcedureYaml>(yaml);
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

    private static StoredProcedureDocument Map(StoredProcedureYaml y, string source)
    {
        var name = YamlDocumentParts.RequireFlowName(y.Name, "a stored-procedure flow", source);
        var connections = YamlDocumentParts.MapConnections(y.Connections, source);

        var procedureYaml = y.Procedure ?? throw new FlowValidationException($"{source}: 'procedure' is required.");

        var server = YamlDocumentParts.ResolveEndpointConnection(
            procedureYaml.Server, procedureYaml.Connection, procedureYaml.Provider,
            "procedure", TargetConnectionName, connections, source);

        // The procedure executes with T-SQL on the resolved server, so it is SQL Server by design; a foreign
        // provider is a configuration error caught at parse time, not deep in the run.
        YamlDocumentParts.RequireSqlServerConnection(connections, server, "procedure", "a stored-procedure flow's server", source);

        var raw = YamlDocumentParts.NullIfBlank(procedureYaml.Object)
            ?? throw new FlowValidationException(
                $"{source}: 'procedure.object' is required (a three-part name like Database.Schema.Procedure).");

        var servicePrincipals = YamlInvokeParts.MapServicePrincipals(y.ServicePrincipals, source);
        var invokes = YamlInvokeParts.MapInvokes(y.Invokes, servicePrincipals, source);
        var parameters = MapParameters(procedureYaml.Parameters, connections, source);

        var flow = new StoredProcedureFlow
        {
            FlowId = YamlDocumentParts.StableFlowId(name),
            SysAlias = name,
            Batch = YamlDocumentParts.NullIfBlank(y.Batch),
            Lifecycle = YamlDocumentParts.ParseLifecycle(y.Lifecycle, source),
            Server = server,
            Procedure = YamlDocumentParts.ParseQualifiedObject(raw, "procedure.object", source),
            Parameters = parameters,
            OnErrorResume = y.OnErrorResume ?? true,
            PostInvokeAlias = YamlInvokeParts.ResolveHookAlias(y.PostInvoke, "postInvoke", invokes, source),
            Description = YamlDocumentParts.NullIfBlank(y.Description),
        };

        return new StoredProcedureDocument
        {
            Flow = flow,
            Connections = connections.Values.ToList(),
            Invokes = invokes.Values.ToList(),
            ServicePrincipals = servicePrincipals.Values.ToList(),
        };
    }

    /// <summary>
    /// Maps the <c>procedure.parameters</c> block. Each entry is either a scalar shorthand (the value is a
    /// literal) or a map declaring <c>selectExp</c>/<c>value</c>/<c>server</c>/<c>prefetch</c>/<c>default</c>.
    /// Declaration order is preserved so prefetched parameters resolve in a predictable sequence.
    /// </summary>
    private static IReadOnlyList<StoredProcedureParameter> MapParameters(
        Dictionary<string, object>? yaml, Dictionary<string, DataSource> connections, string source)
    {
        if (yaml is null || yaml.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<StoredProcedureParameter>(yaml.Count);

        foreach (var (rawName, rawValue) in yaml)
        {
            // Legacy stored the name with its '@'; both forms are accepted and normalised to the bare name so
            // '@Foo' and 'Foo' cannot be declared as two different parameters.
            var name = (YamlDocumentParts.NullIfBlank(rawName) ?? string.Empty).TrimStart('@').Trim();
            if (name.Length == 0)
            {
                throw new FlowValidationException($"{source}: a 'procedure.parameters' entry has an empty name.");
            }

            if (!seen.Add(name))
            {
                throw new FlowValidationException(
                    $"{source}: 'procedure.parameters' declares '{name}' more than once (the leading '@' is optional and ignored).");
            }

            result.Add(MapParameter(name, rawValue, connections, source));
        }

        return result;
    }

    private static StoredProcedureParameter MapParameter(
        string name, object? rawValue, Dictionary<string, DataSource> connections, string source)
    {
        // Scalar shorthand: `MyParam: 42` is the literal form, equivalent to `MyParam: { value: 42 }`.
        if (rawValue is not IDictionary<object, object> map)
        {
            return new StoredProcedureParameter { Name = name, Value = rawValue };
        }

        var dto = new StoredProcedureParameterYaml();
        foreach (var (k, v) in map)
        {
            switch (Convert.ToString(k, System.Globalization.CultureInfo.InvariantCulture)?.Trim().ToLowerInvariant())
            {
                case "selectexp": dto.SelectExp = Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture); break;
                case "value": dto.Value = v; break;
                case "server": dto.Server = Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture); break;
                case "prefetch": dto.Prefetch = ParseBool(v, name, source); break;
                case "default": dto.Default = v; break;
                default:
                    throw new FlowValidationException(
                        $"{source}: parameter '{name}' has unknown key '{k}'. Supported keys are selectExp, value, server, prefetch, default.");
            }
        }

        var selectExp = YamlDocumentParts.NullIfBlank(dto.SelectExp);
        var hasValue = dto.Value is not null;

        // Silently accepting neither would bind DBNull and fail deep inside SQL Server; accepting both would
        // make the precedence a guess. Both are configuration errors worth catching at parse time.
        if (selectExp is null && !hasValue)
        {
            throw new FlowValidationException(
                $"{source}: parameter '{name}' must declare either 'selectExp' (a scalar query resolved at run time) or 'value' (a literal).");
        }

        if (selectExp is not null && hasValue)
        {
            throw new FlowValidationException(
                $"{source}: parameter '{name}' declares both 'selectExp' and 'value'; use exactly one.");
        }

        var server = YamlDocumentParts.NullIfBlank(dto.Server);
        if (server is not null)
        {
            if (selectExp is null)
            {
                throw new FlowValidationException(
                    $"{source}: parameter '{name}' sets 'server' but has no 'selectExp'; a literal value has nothing to evaluate.");
            }

            if (!connections.ContainsKey(server))
            {
                throw new FlowValidationException(
                    $"{source}: parameter '{name}' names connection '{server}', which is not declared in 'connections'.");
            }

            YamlDocumentParts.RequireSqlServerConnection(
                connections, server, $"procedure.parameters.{name}.server", "a stored-procedure parameter query's server", source);
        }

        if (dto.Prefetch == true && selectExp is null)
        {
            throw new FlowValidationException(
                $"{source}: parameter '{name}' sets 'prefetch' but has no 'selectExp'; a literal value needs no resolution order.");
        }

        return new StoredProcedureParameter
        {
            Name = name,
            SelectExp = selectExp,
            Value = dto.Value,
            Server = server,
            Prefetch = dto.Prefetch ?? false,
            Default = dto.Default,
        };
    }

    private static bool ParseBool(object? raw, string parameterName, string source)
    {
        if (raw is bool b)
        {
            return b;
        }

        var text = Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture);
        if (bool.TryParse(text, out var parsed))
        {
            return parsed;
        }

        throw new FlowValidationException(
            $"{source}: parameter '{parameterName}' has a non-boolean 'prefetch' value '{text}'.");
    }
}
