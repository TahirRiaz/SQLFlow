using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Invoke;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Yaml;

/// <summary>
/// A parsed standalone invoke document (flowType: inv): one Azure Data Factory pipeline or Automation runbook
/// trigger plus the document-local service principals it authenticates with. The principals become an in-memory
/// store, so the trigger runs through the exact same resolver and dispatcher as full mode, with no control
/// database anywhere.
/// </summary>
public sealed record InvokeDocument
{
    public required InvokeDefinition Definition { get; init; }

    /// <summary>The document's named service principals (the <c>servicePrincipals:</c> block).</summary>
    public required IReadOnlyList<ServicePrincipalProfile> ServicePrincipals { get; init; }
}

/// <summary>
/// Loads a standalone invoke flow (trigger an ADF pipeline or an Automation runbook and wait for it) from YAML.
/// YamlDotNet handles the grammar; this class is the mapping/validation layer that turns the parsed document
/// into a validated <see cref="InvokeDocument"/>. Parameter scalars keep their YAML types into the parameter
/// JSON (the ADF path is type-preserving).
/// </summary>
public sealed class YamlInvokeFlowLoader
{
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .WithAttemptingUnquotedStringTypeDeserialization()
        .Build();

    public InvokeDocument LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Pipeline file not found: '{path}'.");
        }

        return Parse(File.ReadAllText(path), path);
    }

    public InvokeDocument Parse(string yaml, string source = "<inline>")
    {
        InvokeDocumentYaml? dto;
        try
        {
            dto = _deserializer.Deserialize<InvokeDocumentYaml>(yaml);
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

    private static InvokeDocument Map(InvokeDocumentYaml y, string source)
    {
        var name = YamlDocumentParts.RequireFlowName(y.Name, "an invoke flow", source);
        var servicePrincipals = YamlInvokeParts.MapServicePrincipals(y.ServicePrincipals, source);

        var invokeYaml = y.Invoke ?? throw new FlowValidationException($"{source}: 'invoke' is required.");
        var definition = YamlInvokeParts.MapInvoke(invokeYaml, name, "invoke", servicePrincipals, source);

        return new InvokeDocument
        {
            Definition = definition with
            {
                SysAlias = name,
                Batch = YamlDocumentParts.NullIfBlank(y.Batch),
                Lifecycle = YamlDocumentParts.ParseLifecycle(y.Lifecycle, source),
                OnErrorResume = y.OnErrorResume ?? invokeYaml.OnErrorResume ?? true,
            },
            ServicePrincipals = servicePrincipals.Values.ToList(),
        };
    }
}
