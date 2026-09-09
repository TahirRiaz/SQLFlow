using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Core.Translate;

namespace SqlFlow.Translate;

/// <summary>
/// Renders one document from a compiled template: objects and fixed arrays mirror the template, a
/// <c>$forEach</c> materializes one element per matching dataset row, and leaves pull column values through the
/// declared coercion and null policy. Every error carries the JSON path being rendered and (for column errors)
/// the columns actually in scope, so a template/query mismatch is debuggable from the log line alone.
/// </summary>
public static class JsonTemplateRenderer
{
    private readonly record struct Rendered(bool Omitted, JsonNode? Node)
    {
        public static readonly Rendered Omit = new(true, null);

        public static Rendered Of(JsonNode? node) => new(false, node);
    }

    /// <summary>Renders the document for one scope. A root leaf whose null policy says omit renders as an
    /// explicit JSON null: a document, unlike an object property, cannot be absent.</summary>
    public static JsonNode? Render(
        TranslateNode template, TranslateScope scope, TranslateDatasetIndex datasets, TranslateDocumentNulls nulls)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(datasets);
        return RenderNode(template, scope, datasets, nulls, "$").Node;
    }

    private static Rendered RenderNode(
        TranslateNode node, TranslateScope scope, TranslateDatasetIndex datasets, TranslateDocumentNulls nulls, string path)
    {
        switch (node)
        {
            case TranslateObjectNode obj:
            {
                var result = new JsonObject();
                foreach (var property in obj.Properties)
                {
                    var rendered = RenderNode(property.Value, scope, datasets, nulls, $"{path}.{property.Name}");
                    if (!rendered.Omitted)
                    {
                        result[property.Name] = rendered.Node;
                    }
                }

                return Rendered.Of(result);
            }

            case TranslateListNode list:
            {
                var result = new JsonArray();
                for (var i = 0; i < list.Items.Count; i++)
                {
                    var rendered = RenderNode(list.Items[i], scope, datasets, nulls, $"{path}[{i}]");
                    if (!rendered.Omitted)
                    {
                        result.Add(rendered.Node);
                    }
                }

                return Rendered.Of(result);
            }

            case TranslateArrayNode array:
            {
                var rows = datasets.Resolve(array.ForEach, scope, path);
                var result = new JsonArray();
                for (var i = 0; i < rows.Count; i++)
                {
                    var rendered = RenderNode(array.Item, scope.Push(rows[i]), datasets, nulls, $"{path}[{i}]");
                    if (!rendered.Omitted)
                    {
                        result.Add(rendered.Node);
                    }
                }

                return Rendered.Of(result);
            }

            case TranslateRowNode row:
            {
                var rows = datasets.Resolve(row.Row, scope, path);
                if (rows.Count != 1)
                {
                    throw new SqlFlowException(
                        $"At {path}: '$row: {row.Row}' requires exactly one row, found {rows.Count}. " +
                        (rows.Count == 0
                            ? "A header dataset must return its row; a bound one-to-one dataset must have a match for this scope."
                            : "Narrow the dataset query, or use '$forEach' if the block genuinely repeats."));
                }

                return RenderNode(row.Item, scope.Push(rows[0]), datasets, nulls, path);
            }

            case TranslateValueNode leaf:
                return RenderLeaf(leaf, scope, nulls, path);

            default:
                throw new SqlFlowException($"At {path}: unknown template node '{node.GetType().Name}'.");
        }
    }

    private static Rendered RenderLeaf(TranslateValueNode leaf, TranslateScope scope, TranslateDocumentNulls nulls, string path)
    {
        switch (leaf.Source)
        {
            case TranslateValueSource.Constant:
                // A fresh instance per document: a JsonNode has a single parent, so constants re-parse.
                return Rendered.Of(JsonNode.Parse(leaf.ConstantJson ?? "null"));

            case TranslateValueSource.Column:
            {
                if (!scope.TryGet(leaf.Column!, out var value))
                {
                    throw MissingColumn(leaf.Column!, scope, path);
                }

                return value is null or DBNull
                    ? ApplyNullPolicy(leaf, nulls)
                    : Rendered.Of(Coerce(value, leaf.Type, leaf.Format, path));
            }

            case TranslateValueSource.Template:
            {
                var segments = TranslateTemplateText.Parse(leaf.Template!);
                var builder = new StringBuilder();
                var tokenCount = 0;
                var nullTokens = 0;
                foreach (var segment in segments)
                {
                    if (!segment.IsToken)
                    {
                        builder.Append(segment.Text);
                        continue;
                    }

                    tokenCount++;
                    if (!scope.TryGet(segment.Text, out var value))
                    {
                        throw MissingColumn(segment.Text, scope, path);
                    }

                    if (value is null or DBNull)
                    {
                        nullTokens++;
                    }
                    else
                    {
                        builder.Append(FormatScalar(value, path));
                    }
                }

                // Every referenced column NULL means the template has no data: apply the null policy rather
                // than emitting the bare literal skeleton. A partially-null template renders with empty slots.
                if (tokenCount > 0 && nullTokens == tokenCount)
                {
                    return ApplyNullPolicy(leaf, nulls);
                }

                var text = builder.ToString();
                return Rendered.Of(leaf.Type == TranslateValueType.Auto
                    ? JsonValue.Create(text)
                    : Coerce(text, leaf.Type, leaf.Format, path));
            }

            default:
                throw new SqlFlowException($"At {path}: unknown leaf source '{leaf.Source}'.");
        }
    }

    private static Rendered ApplyNullPolicy(TranslateValueNode leaf, TranslateDocumentNulls nulls)
    {
        var policy = leaf.WhenNull == TranslateNullPolicy.Inherit
            ? (nulls == TranslateDocumentNulls.Omit ? TranslateNullPolicy.Omit : TranslateNullPolicy.Null)
            : leaf.WhenNull;

        return policy switch
        {
            TranslateNullPolicy.Omit => Rendered.Omit,
            TranslateNullPolicy.Default => Rendered.Of(JsonNode.Parse(leaf.DefaultJson ?? "null")),
            _ => Rendered.Of(null),
        };
    }

    private static SqlFlowException MissingColumn(string column, TranslateScope scope, string path)
    {
        var available = scope.AvailableColumns();
        return new SqlFlowException(
            $"At {path}: column '{column}' is not in scope. " +
            (available.Count == 0
                ? "No columns are in scope here (a result-set-grain root has none; reference columns inside a $forEach)."
                : $"Columns in scope: {string.Join(", ", available)}."));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Value coercion
    // ---------------------------------------------------------------------------------------------------------

    private static JsonNode? Coerce(object value, TranslateValueType type, string? format, string path)
    {
        try
        {
            return type switch
            {
                TranslateValueType.Auto => AutoNode(value, path),
                TranslateValueType.String => JsonValue.Create(FormatScalar(value, path)),
                TranslateValueType.Int => JsonValue.Create(Convert.ToInt32(value, CultureInfo.InvariantCulture)),
                TranslateValueType.Long => JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
                TranslateValueType.Double => JsonValue.Create(FiniteDouble(Convert.ToDouble(value, CultureInfo.InvariantCulture), path)),
                TranslateValueType.Decimal => JsonValue.Create(Convert.ToDecimal(value, CultureInfo.InvariantCulture)),
                TranslateValueType.Bool => JsonValue.Create(ToBool(value)),
                TranslateValueType.Date => JsonValue.Create(ToDateTime(value).ToString(format ?? "yyyy-MM-dd", CultureInfo.InvariantCulture)),
                TranslateValueType.DateTime => JsonValue.Create(
                    format is null ? IsoDateTime(ToDateTime(value)) : ToDateTime(value).ToString(format, CultureInfo.InvariantCulture)),
                TranslateValueType.Json => ParseEmbeddedJson(value, path),
                _ => throw new SqlFlowException($"At {path}: unknown value type '{type}'."),
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new SqlFlowException(
                $"At {path}: cannot convert value '{value}' ({value.GetType().Name}) to {type}: {ex.Message}", ex);
        }
    }

    /// <summary>The native JSON mapping: numbers stay numbers, booleans booleans, temporal values become ISO
    /// strings, binary becomes base64. An unsupported provider type is an error, never a silent ToString.</summary>
    private static JsonNode? AutoNode(object value, string path)
        => value switch
        {
            bool b => JsonValue.Create(b),
            byte or sbyte or short or ushort or int or uint or long
                => JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
            ulong ul => JsonValue.Create(ul),
            float f => JsonValue.Create(FiniteDouble(f, path)),
            double d => JsonValue.Create(FiniteDouble(d, path)),
            decimal m => JsonValue.Create(m),
            string s => JsonValue.Create(s),
            char c => JsonValue.Create(c.ToString()),
            Guid g => JsonValue.Create(g.ToString("D")),
            DateTime dt => JsonValue.Create(IsoDateTime(dt)),
            DateTimeOffset dto => JsonValue.Create(IsoDateTimeOffset(dto)),
            DateOnly date => JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            TimeOnly time => JsonValue.Create(IsoTime(time)),
            TimeSpan span => JsonValue.Create(span.ToString("c", CultureInfo.InvariantCulture)),
            byte[] bytes => JsonValue.Create(Convert.ToBase64String(bytes)),
            _ => throw new SqlFlowException(
                $"At {path}: value type '{value.GetType().Name}' has no JSON mapping; cast it in the query or use $type."),
        };

    /// <summary>The invariant string form of a scalar, used by templates and <c>$type: string</c>. Temporal
    /// values use the same ISO forms as the native mapping, so a templated date equals a passthrough date.</summary>
    private static string FormatScalar(object value, string path)
        => value switch
        {
            string s => s,
            bool b => b ? "true" : "false",
            DateTime dt => IsoDateTime(dt),
            DateTimeOffset dto => IsoDateTimeOffset(dto),
            DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly time => IsoTime(time),
            byte[] bytes => Convert.ToBase64String(bytes),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
                ?? throw new SqlFlowException($"At {path}: value type '{value.GetType().Name}' has no string form."),
        };

    private static double FiniteDouble(double value, string path)
        => double.IsFinite(value)
            ? value
            : throw new SqlFlowException($"At {path}: value '{value}' is not representable in JSON (NaN/Infinity).");

    private static bool ToBool(object value)
        => value switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            string s when s.Trim() is "1" => true,
            string s when s.Trim() is "0" => false,
            _ => Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0,
        };

    private static DateTime ToDateTime(object value)
        => value switch
        {
            DateTime dt => dt,
            DateTimeOffset dto => dto.DateTime,
            DateOnly date => date.ToDateTime(TimeOnly.MinValue),
            string s => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.None),
            _ => Convert.ToDateTime(value, CultureInfo.InvariantCulture),
        };

    private static JsonNode? ParseEmbeddedJson(object value, string path)
    {
        if (value is not string json)
        {
            throw new SqlFlowException(
                $"At {path}: '$type: json' embeds a JSON string column; the value is {value.GetType().Name}.");
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new SqlFlowException($"At {path}: the column value is not valid JSON: {ex.Message}", ex);
        }
    }

    /// <summary>ISO 8601 with the fractional part only when the value carries one, so whole-second values stay
    /// compact and sub-second precision survives.</summary>
    private static string IsoDateTime(DateTime value)
        => value.ToString(
            value.Ticks % TimeSpan.TicksPerSecond == 0 ? "yyyy-MM-dd'T'HH:mm:ss" : "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
            CultureInfo.InvariantCulture);

    private static string IsoDateTimeOffset(DateTimeOffset value)
        => value.ToString(
            value.Ticks % TimeSpan.TicksPerSecond == 0 ? "yyyy-MM-dd'T'HH:mm:ssK" : "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
            CultureInfo.InvariantCulture);

    private static string IsoTime(TimeOnly value)
        => value.ToString(
            value.Ticks % TimeSpan.TicksPerSecond == 0 ? "HH:mm:ss" : "HH:mm:ss.FFFFFFF",
            CultureInfo.InvariantCulture);
}
