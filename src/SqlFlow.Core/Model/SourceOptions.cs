using System.Globalization;

namespace SqlFlow.Core.Model;

/// <summary>Typed accessors over a source's free-form option bag, shared by all readers.</summary>
public static class SourceOptions
{
    /// <summary>
    /// The option the engine sets on every run of a flow whose incremental watermark is the file date: a file reader
    /// reads only files whose modified second has ended. It waits out the second its listing started in when a file
    /// of that second is found, then lists again, and leaves a file modified after that, or dated ahead of the clock,
    /// for the next run. The watermark is stored to the whole second, so a file loaded during the second it was
    /// written would let a later file of the same second fall behind the watermark for good. The engine injects it; a
    /// flow author does not write it.
    /// </summary>
    public const string SettledFilesOnly = "settledFilesOnly";

    public static string GetString(this IReadOnlyDictionary<string, string?> options, string key, string fallback)
        => options.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    public static bool GetBool(this IReadOnlyDictionary<string, string?> options, string key, bool fallback)
        => options.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : fallback;

    public static int GetInt(this IReadOnlyDictionary<string, string?> options, string key, int fallback)
        => options.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
            ? i
            : fallback;
}
