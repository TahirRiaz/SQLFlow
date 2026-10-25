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

    /// <summary>The strings of the array the reader stands at the start of, up to its end; <paramref name="name"/> labels errors.</summary>
    internal static List<string> ReadItems(ref Utf8JsonReader reader, string name)
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

/// <summary>
/// The JSON form of one run value (<see cref="RunValueList"/>): a string, or an array of strings for a list of values, as
/// <see cref="RunValuesJsonConverter"/> carries a run's whole map. Put on a single value that crosses a JSON boundary (a
/// parameter's default, the value a run takes for it), so a client reads a list as the array it is.
/// </summary>
public sealed class RunValueJsonConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString()!,
            JsonTokenType.StartArray => RunValueList.Of(RunValuesJsonConverter.ReadItems(ref reader, "value")),
            _ => throw new JsonException("A run value must be a string or an array of strings."),
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        if (!RunValueList.IsList(value))
        {
            writer.WriteStringValue(value);
            return;
        }

        writer.WriteStartArray();
        foreach (var item in RunValueList.Items(value))
        {
            writer.WriteStringValue(item);
        }

        writer.WriteEndArray();
    }
}
