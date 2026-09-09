using System.Text.Json;
using SqlFlow.Core;

namespace SqlFlow.Azure.Invoke;

/// <summary>
/// Converts an flw.Invoke <c>ParameterJSON</c> object into the parameter shape each Azure target expects: an
/// <c>IDictionary&lt;string, BinaryData&gt;</c> of raw JSON values for a Data Factory pipeline run, and an
/// <c>IDictionary&lt;string, string&gt;</c> for an Automation runbook job (where the contract is string→string,
/// so a structured value is passed as its JSON text for the runbook to parse). A blank value yields an empty
/// dictionary; a non-object or malformed JSON fails fast with a clear message.
/// </summary>
public static class InvokeParameterJson
{
    public static IDictionary<string, BinaryData> ToDataFactoryParameters(string? json)
    {
        var result = new Dictionary<string, BinaryData>(StringComparer.Ordinal);
        using var doc = ParseObject(json);
        if (doc is null)
        {
            return result;
        }

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            // The Data Factory SDK takes each value as raw JSON, preserving numbers/bools/objects/arrays.
            result[property.Name] = BinaryData.FromString(property.Value.GetRawText());
        }

        return result;
    }

    public static IDictionary<string, string> ToAutomationParameters(string? json)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var doc = ParseObject(json);
        if (doc is null)
        {
            return result;
        }

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            // Automation parameters are string→string; a JSON string is unwrapped, anything structured is
            // passed as its JSON text for the runbook to deserialize.
            result[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText();
        }

        return result;
    }

    private static JsonDocument? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new SqlFlowException($"Invoke ParameterJSON is not valid JSON: {ex.Message}", ex);
        }

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            doc.Dispose();
            throw new SqlFlowException("Invoke ParameterJSON must be a JSON object of parameter name/value pairs.");
        }

        return doc;
    }
}
