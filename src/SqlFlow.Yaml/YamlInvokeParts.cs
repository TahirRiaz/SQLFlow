using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Invoke;

namespace SqlFlow.Yaml;

/// <summary>
/// The shared mapping/validation of the invoke surface: the <c>servicePrincipals:</c> and <c>invokes:</c>
/// blocks (available in the ing/exp/sp documents and the standalone <c>flowType: inv</c> document), plus the
/// YAML-to-JSON parameter conversion. Every loader goes through these helpers so the invoke dialect stays one
/// dialect: the same fields, the same secretless gate, and the same error wording in every document kind.
/// </summary>
internal static partial class YamlInvokeParts
{
    /// <summary>Maps the document's <c>servicePrincipals:</c> block. The secretless gate runs here with the
    /// YAML path in the error: a clientSecret may only be a whole <c>${...}</c> reference, never a value.</summary>
    public static Dictionary<string, ServicePrincipalProfile> MapServicePrincipals(
        Dictionary<string, ServicePrincipalYaml>? block, string source)
    {
        var profiles = new Dictionary<string, ServicePrincipalProfile>(StringComparer.OrdinalIgnoreCase);
        if (block is null)
        {
            return profiles;
        }

        foreach (var (rawName, sp) in block)
        {
            var name = rawName?.Trim() ?? string.Empty;
            if (!IsValidName(name))
            {
                throw new FlowValidationException(
                    $"{source}: service-principal name '{rawName}' is invalid. Use letters, digits, '_', '.', or '-'.");
            }

            if (sp is null)
            {
                throw new FlowValidationException(
                    $"{source}: 'servicePrincipals.{name}' must be a map of service-principal fields.");
            }

            var clientSecret = YamlDocumentParts.NullIfBlank(sp.ClientSecret)?.Trim();
            if (clientSecret is not null && !WholeReference().IsMatch(clientSecret))
            {
                throw new FlowValidationException(
                    $"{source}: 'servicePrincipals.{name}.clientSecret' must be a whole ${{env:NAME}} or " +
                    "${keyvault:vault/secret} reference; the secret value itself never rests in the document. " +
                    "Omit it to authenticate with the ambient Azure credential.");
            }

            var tenantId = YamlDocumentParts.NullIfBlank(sp.TenantId)?.Trim();
            var clientId = YamlDocumentParts.NullIfBlank(sp.ClientId)?.Trim();
            if (clientSecret is not null && (tenantId is null || clientId is null))
            {
                throw new FlowValidationException(
                    $"{source}: 'servicePrincipals.{name}' sets clientSecret, so tenantId and clientId are required.");
            }

            var subscriptionId = YamlDocumentParts.NullIfBlank(sp.SubscriptionId)?.Trim()
                ?? throw new FlowValidationException($"{source}: 'servicePrincipals.{name}.subscriptionId' is required.");
            var resourceGroup = YamlDocumentParts.NullIfBlank(sp.ResourceGroup)?.Trim()
                ?? throw new FlowValidationException($"{source}: 'servicePrincipals.{name}.resourceGroup' is required.");

            var profile = new ServicePrincipalProfile
            {
                Alias = name,
                TenantId = tenantId,
                ClientId = clientId,
                ClientSecretRef = clientSecret,
                SubscriptionId = subscriptionId,
                ResourceGroup = resourceGroup,
                DataFactoryName = YamlDocumentParts.NullIfBlank(sp.DataFactoryName)?.Trim(),
                AutomationAccountName = YamlDocumentParts.NullIfBlank(sp.AutomationAccountName)?.Trim(),
                KeyVaultName = YamlDocumentParts.NullIfBlank(sp.KeyVaultName)?.Trim(),
            };

            if (!profiles.TryAdd(name, profile))
            {
                throw new FlowValidationException($"{source}: service principal '{name}' is declared more than once.");
            }
        }

        return profiles;
    }

    /// <summary>Maps the document's <c>invokes:</c> block to <see cref="InvokeDefinition"/>s. The cross-field
    /// requirements fail here, not mid-run: an adf invoke needs its pipeline (and its service principal needs a
    /// dataFactoryName), an aut invoke its runbook (and an automationAccountName); the mismatched field is
    /// rejected rather than silently ignored.</summary>
    public static Dictionary<string, InvokeDefinition> MapInvokes(
        Dictionary<string, InvokeBlockYaml>? block,
        Dictionary<string, ServicePrincipalProfile> servicePrincipals,
        string source)
    {
        var invokes = new Dictionary<string, InvokeDefinition>(StringComparer.OrdinalIgnoreCase);
        if (block is null)
        {
            return invokes;
        }

        foreach (var (rawName, node) in block)
        {
            var name = rawName?.Trim() ?? string.Empty;
            if (!IsValidName(name))
            {
                throw new FlowValidationException(
                    $"{source}: invoke name '{rawName}' is invalid. Use letters, digits, '_', '.', or '-'.");
            }

            if (node is null)
            {
                throw new FlowValidationException($"{source}: 'invokes.{name}' must be a map of invoke fields.");
            }

            var definition = MapInvoke(node, name, $"invokes.{name}", servicePrincipals, source);
            if (!invokes.TryAdd(name, definition))
            {
                throw new FlowValidationException($"{source}: invoke '{name}' is declared more than once.");
            }
        }

        return invokes;
    }

    /// <summary>Maps one invoke (an entry under <c>invokes:</c>, or the standalone document's <c>invoke:</c>
    /// section at <paramref name="section"/>).</summary>
    public static InvokeDefinition MapInvoke(
        InvokeBlockYaml node, string alias, string section,
        Dictionary<string, ServicePrincipalProfile> servicePrincipals, string source)
    {
        var type = ParseInvokeType(node.Type, $"{section}.type", source);
        var pipeline = YamlDocumentParts.NullIfBlank(node.Pipeline)?.Trim();
        var runbook = YamlDocumentParts.NullIfBlank(node.Runbook)?.Trim();

        if (type == InvokeType.AzureDataFactory)
        {
            if (pipeline is null)
            {
                throw new FlowValidationException($"{source}: '{section}.pipeline' is required when type is adf.");
            }

            if (runbook is not null)
            {
                throw new FlowValidationException(
                    $"{source}: '{section}.runbook' is set, but type adf runs a pipeline; remove it or use type aut.");
            }
        }
        else
        {
            if (runbook is null)
            {
                throw new FlowValidationException($"{source}: '{section}.runbook' is required when type is aut.");
            }

            if (pipeline is not null)
            {
                throw new FlowValidationException(
                    $"{source}: '{section}.pipeline' is set, but type aut runs a runbook; remove it or use type adf.");
            }
        }

        var spName = YamlDocumentParts.NullIfBlank(node.ServicePrincipal)?.Trim()
            ?? throw new FlowValidationException(
                $"{source}: '{section}.servicePrincipal' is required (a name declared under 'servicePrincipals:').");
        if (!servicePrincipals.TryGetValue(spName, out var profile))
        {
            throw new FlowValidationException(
                $"{source}: '{section}.servicePrincipal' references '{spName}', which is not declared under 'servicePrincipals:'.");
        }

        if (type == InvokeType.AzureDataFactory && profile.DataFactoryName is null)
        {
            throw new FlowValidationException(
                $"{source}: '{section}' is an adf invoke, but service principal '{spName}' has no dataFactoryName.");
        }

        if (type == InvokeType.AzureAutomation && profile.AutomationAccountName is null)
        {
            throw new FlowValidationException(
                $"{source}: '{section}' is an aut invoke, but service principal '{spName}' has no automationAccountName.");
        }

        return new InvokeDefinition
        {
            FlowId = YamlDocumentParts.StableFlowId(alias),
            InvokeAlias = alias,
            InvokeType = type,
            PipelineName = pipeline,
            RunbookName = runbook,
            ParameterJson = ParametersToJson(node.Parameters, $"{section}.parameters", source),
            Outputs = FileOutputMapping.Map(node.Output, node.Outputs, section, source),
            OnErrorResume = node.OnErrorResume ?? true,
            TargetServicePrincipalReference = "@" + spName,
        };
    }

    /// <summary>Validates a pre/post invoke hook reference against the document's declared invokes.</summary>
    public static string? ResolveHookAlias(
        string? alias, string field, Dictionary<string, InvokeDefinition> invokes, string source)
    {
        var trimmed = YamlDocumentParts.NullIfBlank(alias)?.Trim();
        if (trimmed is null)
        {
            return null;
        }

        if (!invokes.ContainsKey(trimmed))
        {
            throw new FlowValidationException(
                $"{source}: '{field}' references '{trimmed}', which is not declared under 'invokes:'.");
        }

        return trimmed;
    }

    private static InvokeType ParseInvokeType(string? value, string field, string source)
    {
        try
        {
            return InvokeTypeCodes.Parse(value);
        }
        catch (SqlFlowException ex)
        {
            throw new FlowValidationException($"{source}: '{field}': {ex.Message}", ex);
        }
    }

    /// <summary>Converts the YAML parameters map to the JSON object string the executors expect. Scalar types
    /// survive the trip (the ADF path is type-preserving): a plain <c>true</c> or <c>42</c> in the YAML arrives
    /// as a JSON boolean or number, a quoted scalar as a string, and nested maps/sequences as objects/arrays.</summary>
    public static string? ParametersToJson(Dictionary<string, object?>? parameters, string field, string source)
    {
        if (parameters is null || parameters.Count == 0)
        {
            return null;
        }

        var root = new JsonObject();
        foreach (var (name, value) in parameters)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new FlowValidationException($"{source}: '{field}' has a parameter with an empty name.");
            }

            root[name] = ToNode(value, $"{field}.{name}", source);
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static JsonNode? ToNode(object? value, string path, string source)
        => value switch
        {
            null => null,
            string s => JsonValue.Create(s),
            bool b => JsonValue.Create(b),
            sbyte or byte or short or ushort or int or uint or long
                => JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
            ulong ul => JsonValue.Create(ul),
            float or double => JsonValue.Create(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
            decimal m => JsonValue.Create(m),
            IDictionary<object, object?> map => MapToObject(map, path, source),
            IEnumerable<object?> list => ListToArray(list, path, source),
            _ => throw new FlowValidationException(
                $"{source}: '{path}' has an unsupported value of type '{value.GetType().Name}'. " +
                "Use scalars, maps, or sequences."),
        };

    private static JsonObject MapToObject(IDictionary<object, object?> map, string path, string source)
    {
        var node = new JsonObject();
        foreach (var (key, value) in map)
        {
            var name = key as string ?? Convert.ToString(key, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new FlowValidationException($"{source}: '{path}' has a nested key that is empty.");
            }

            node[name] = ToNode(value, $"{path}.{name}", source);
        }

        return node;
    }

    private static JsonArray ListToArray(IEnumerable<object?> list, string path, string source)
    {
        var node = new JsonArray();
        var index = 0;
        foreach (var item in list)
        {
            node.Add(ToNode(item, $"{path}[{index}]", source));
            index++;
        }

        return node;
    }

    private static bool IsValidName(string name)
        => name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');

    [GeneratedRegex(@"^\$\{[a-zA-Z]+:[^}]+\}$")]
    private static partial Regex WholeReference();
}
