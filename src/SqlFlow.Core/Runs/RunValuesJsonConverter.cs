using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlFlow.Core.Runs;

/// <summary>
/// The JSON form of a run's values (<see cref="RunParameters.Values"/>): an object mapping each parameter name to a string,
/// or to an array of strings when the run gives the parameter several values (<see cref="RunValueList"/>). Read, an array
/// becomes the list's text; written, a list's text becomes its array again, so a client never parses the text form. Put
/// on every property that carries run values across a JSON boundary.
/// </summary>
public sealed class RunValuesJsonConverter : JsonConverter<IReadOnlyDictionary<string, string>>
{
    public override IReadOnlyDictionary<string, string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Run values must be a JSON object mapping each parameter name to a string or an array of strings.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return values;
            }

            var name = reader.GetString() ?? string.Empty;
            reader.Read();
            values[name] = reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString()!,
                JsonTokenType.StartArray => RunValueList.Of(ReadItems(ref reader, name)),
                _ => throw new JsonException($"Parameter '{name}' must be a string or an array of strings."),
            };
        }

        throw new JsonException("Run values end before their object closes.");
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<string, string> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartObject();
        foreach (var (name, text) in value)
        {
            if (RunValueList.IsList(text))
            {
                writer.WriteStartArray(name);
                foreach (var item in RunValueList.Items(text))
                {
                    writer.WriteStringValue(item);
                }

                writer.WriteEndArray();
            }
            else
            {
                writer.WriteString(name, text);
            }
        }

        writer.WriteEndObject();
    }

    private static List<string> ReadItems(ref Utf8JsonReader reader, string name)
    {
        var items = new List<string>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.EndArray:
                    return items;
                case JsonTokenType.String:
                    items.Add(reader.GetString()!);
                    break;
                default:
                    throw new JsonException($"Parameter '{name}' must be a string or an array of strings; its array holds a {reader.TokenType}.");
            }
        }

        throw new JsonException($"Parameter '{name}' ends before its array closes.");
    }
}
