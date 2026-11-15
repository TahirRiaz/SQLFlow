using System.Text.Json;
using SqlFlow.Core;
using SqlFlow.Core.Dataverse;

namespace SqlFlow.Dataverse;

/// <summary>
/// Reads a table's metadata through the Web API on every run: the table's collection name and primary key, then its
/// columns, then the type-specific facts the target types depend on (text lengths, date behaviours, lookup targets),
/// each through the attribute collection cast to its metadata type. Reading it every run is what lets a column added,
/// widened or removed in Power Apps reach the target through the engine's schema evolution with no change to the flow.
/// </summary>
internal static class DataverseMetadataReader
{
    public static async Task<DataverseTableMetadata> ReadAsync(DataverseClient client, DataverseSourceOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        // The logical name was validated as letters, digits and underscores, so it is safe inside the key literal.
        var definition = $"EntityDefinitions(LogicalName='{options.Table}')";
        var what = $"the metadata of the table '{options.Table}'";

        string entitySet;
        string primaryId;
        using (var table = await client.GetJsonAsync(
            new Uri(options.ApiRoot, $"{definition}?$select=LogicalName,EntitySetName,PrimaryIdAttribute"), null, what, ct).ConfigureAwait(false))
        {
            entitySet = RequiredString(table.RootElement, "EntitySetName", what);
            primaryId = RequiredString(table.RootElement, "PrimaryIdAttribute", what);
        }

        var attributes = new List<DataverseAttribute>();
        using (var listed = await client.GetJsonAsync(
            new Uri(options.ApiRoot, $"{definition}/Attributes?$select=LogicalName,AttributeType,AttributeTypeName,IsValidForRead,IsValidODataAttribute,AttributeOf"),
            null, what, ct).ConfigureAwait(false))
        {
            foreach (var item in Values(listed.RootElement, what))
            {
                attributes.Add(new DataverseAttribute
                {
                    LogicalName = RequiredString(item, "LogicalName", what).ToLowerInvariant(),
                    AttributeType = RequiredString(item, "AttributeType", what),
                    AttributeTypeName = item.TryGetProperty("AttributeTypeName", out var typeName)
                        && typeName.ValueKind == JsonValueKind.Object
                        && typeName.TryGetProperty("Value", out var typeValue)
                        && typeValue.ValueKind == JsonValueKind.String
                            ? typeValue.GetString()
                            : null,
                    IsValidForRead = Bool(item, "IsValidForRead"),
                    IsValidODataAttribute = Bool(item, "IsValidODataAttribute"),
                    AttributeOf = item.TryGetProperty("AttributeOf", out var of) && of.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(of.GetString())
                        ? of.GetString()!.ToLowerInvariant()
                        : null,
                });
            }
        }

        if (attributes.Count == 0)
        {
            throw new SqlFlowException($"Reading {what}: the Dataverse environment {options.Environment} listed no columns for it.");
        }

        // The type-specific facts, each asked only when the table has a column of that type.
        var lengths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var behaviours = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var targets = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        if (attributes.Any(a => DataverseReadPlan.TypeName(a) == "StringType"))
        {
            await ReadCastAsync(client, options, definition, "StringAttributeMetadata", "LogicalName,MaxLength,DatabaseLength", what, item =>
            {
                if (Length(item) is { } length)
                {
                    lengths[RequiredString(item, "LogicalName", what)] = length;
                }
            }, ct).ConfigureAwait(false);
        }

        if (attributes.Any(a => DataverseReadPlan.TypeName(a) == "MemoType"))
        {
            await ReadCastAsync(client, options, definition, "MemoAttributeMetadata", "LogicalName,MaxLength", what, item =>
            {
                if (Length(item) is { } length)
                {
                    lengths[RequiredString(item, "LogicalName", what)] = length;
                }
            }, ct).ConfigureAwait(false);
        }

        if (attributes.Any(a => DataverseReadPlan.TypeName(a) == "DateTimeType"))
        {
            await ReadCastAsync(client, options, definition, "DateTimeAttributeMetadata", "LogicalName,DateTimeBehavior", what, item =>
            {
                if (item.TryGetProperty("DateTimeBehavior", out var behaviour)
                    && behaviour.ValueKind == JsonValueKind.Object
                    && behaviour.TryGetProperty("Value", out var value)
                    && value.ValueKind == JsonValueKind.String)
                {
                    behaviours[RequiredString(item, "LogicalName", what)] = value.GetString()!;
                }
            }, ct).ConfigureAwait(false);
        }

        if (attributes.Any(a => DataverseReadPlan.TypeName(a) == "LookupType"))
        {
            await ReadCastAsync(client, options, definition, "LookupAttributeMetadata", "LogicalName,Targets", what, item =>
            {
                if (item.TryGetProperty("Targets", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    targets[RequiredString(item, "LogicalName", what)] = list.EnumerateArray()
                        .Where(t => t.ValueKind == JsonValueKind.String)
                        .Select(t => t.GetString()!)
                        .ToList();
                }
            }, ct).ConfigureAwait(false);
        }

        return new DataverseTableMetadata
        {
            LogicalName = options.Table,
            EntitySetName = entitySet,
            PrimaryIdAttribute = primaryId.ToLowerInvariant(),
            Attributes = attributes.Select(a => a with
            {
                MaxLength = lengths.TryGetValue(a.LogicalName, out var length) ? length : null,
                DateTimeBehavior = behaviours.TryGetValue(a.LogicalName, out var behaviour) ? behaviour : null,
                Targets = targets.TryGetValue(a.LogicalName, out var to) ? to : [],
            }).ToList(),
        };
    }

    private static async Task ReadCastAsync(
        DataverseClient client, DataverseSourceOptions options, string definition, string metadataType, string select, string what,
        Action<JsonElement> each, CancellationToken ct)
    {
        using var document = await client.GetJsonAsync(
            new Uri(options.ApiRoot, $"{definition}/Attributes/Microsoft.Dynamics.CRM.{metadataType}?$select={select}"),
            null, what, ct).ConfigureAwait(false);
        foreach (var item in Values(document.RootElement, what))
        {
            each(item);
        }
    }

    /// <summary>A text column's length: the length its database column was made with when the metadata gives it (it never
    /// shrinks when the column's maximum is lowered later, and older rows may still be that long), else its maximum.</summary>
    private static int? Length(JsonElement item)
    {
        int? Read(string property)
            => item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0
                ? number
                : null;

        var max = Read("MaxLength");
        var database = Read("DatabaseLength");
        return (max, database) switch
        {
            ({ } m, { } d) => Math.Max(m, d),
            ({ } m, null) => m,
            (null, { } d) => d,
            _ => null,
        };
    }

    private static IEnumerable<JsonElement> Values(JsonElement root, string what)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : throw new SqlFlowException($"Reading {what}: the Dataverse answer has no 'value' list.");

    private static string RequiredString(JsonElement item, string property, string what)
        => item.ValueKind == JsonValueKind.Object
           && item.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new SqlFlowException($"Reading {what}: the Dataverse answer has no '{property}'.");

    private static bool Bool(JsonElement item, string property)
        => item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
}
