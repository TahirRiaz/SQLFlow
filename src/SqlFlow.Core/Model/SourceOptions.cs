using System.Globalization;

namespace SqlFlow.Core.Model;

/// <summary>Typed accessors over a source's free-form option bag, shared by all readers.</summary>
public static class SourceOptions
{
    public static string GetString(this IReadOnlyDictionary<string, string?> options, string key, string fallback)
        => options.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    public static bool GetBool(this IReadOnlyDictionary<string, string?> options, string key, bool fallback)
        => options.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : fallback;

    public static int GetInt(this IReadOnlyDictionary<string, string?> options, string key, int fallback)
        => options.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
            ? i
            : fallback;
}
