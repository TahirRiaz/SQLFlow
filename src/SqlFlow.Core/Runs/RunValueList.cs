using System.Text.Encodings.Web;
using System.Text.Json;

namespace SqlFlow.Core.Runs;

/// <summary>
/// A run value that holds several values (a flow parameter a run selects more than one value of, say). A run's values
/// (<see cref="RunParameters.Values"/>) map each parameter name to one text: a single value is its own text, and a list
/// of values is the compact JSON array of them, which this class writes and reads. Every JSON boundary (a trigger, a
/// schedule, a run's detail, the stored row, the node protocol) carries a list as a real array through
/// <see cref="RunValuesJsonConverter"/>, so only code holding the map sees the text form, and it reads it through
/// <see cref="Items"/>. Whether a parameter accepts several values is decided by the kind that owns the flow, which
/// refuses a list it cannot apply.
/// </summary>
public static class RunValueList
{
    /// <summary>The most values one parameter can be given.</summary>
    public const int MaxItems = 100;

    /// <summary>The longest accepted list, as its compact JSON text: bounded so the run row and the protocol message
    /// stay small, as a payload is.</summary>
    public const int MaxListLength = 16_000;

    private static readonly JsonSerializerOptions TextOptions = new()
    {
        // The text is a key a kind may compare and keep (a scope a watermark is held under), and an operator reads it in
        // run logs: letters outside ASCII stay as they are rather than becoming escapes. It is never written into HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The text of <paramref name="items"/> as one run value: the value itself when there is one (unless it would read as
    /// a list), and the compact JSON array of them otherwise, in the order given.
    /// </summary>
    public static string Of(IEnumerable<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = items.ToList();
        if (list.Any(item => item is null))
        {
            throw new ArgumentException("A run value list holds no null value.", nameof(items));
        }

        return list.Count == 1 && !IsList(list[0]) ? list[0] : JsonSerializer.Serialize(list, TextOptions);
    }

    /// <summary>True when <paramref name="value"/> is the text of a list: a JSON array of strings.</summary>
    public static bool IsList(string? value) => TryParse(value, out _);

    /// <summary>The values <paramref name="value"/> holds: the items of a list, or the value alone.</summary>
    public static IReadOnlyList<string> Items(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return TryParse(value, out var items) ? items : [value];
    }

    private static bool TryParse(string? value, out IReadOnlyList<string> items)
    {
        items = [];
        if (value is null || value.Length < 2 || value[0] != '[')
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var parsed = new List<string>(document.RootElement.GetArrayLength());
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                parsed.Add(element.GetString()!);
            }

            items = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
