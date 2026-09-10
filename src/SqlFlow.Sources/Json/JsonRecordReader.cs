using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SqlFlow.Core;

namespace SqlFlow.Sources.Json;

/// <summary>
/// Turns a JSON stream into a sequence of records, auto-detecting the three shapes the original SQLFlow JSON
/// ingestion accepted: a single object, a top-level array of objects, and newline-delimited JSON (one object
/// per line). The configured root path selects where the records live; once navigated, an array fans out to
/// one record per element and an object yields a single record.
///
/// <para>
/// The read is STREAMING: the file is never materialized. A top-level array is walked element by element, so
/// only one record and a small buffer are resident no matter how large the file is (see
/// <see cref="JsonTokenStream"/>). The three shapes need no separate parsing modes and no "parse the whole
/// document, and on failure re-parse it line by line" fallback, because they are all the same thing to a
/// streaming reader: a sequence of top-level values, each navigated and expanded on its own. The one shape
/// that still has to be held whole is a single value the caller asked for in full - a lone giant object, or
/// <see cref="ReadTopLevelDocumentsAsync"/>'s per-document view.
/// </para>
///
/// The backing <see cref="JsonDocument"/> is kept alive only for the duration of one iteration step, so a
/// consumer must finish with each yielded <see cref="JsonElement"/> (typically by flattening it) before
/// requesting the next. The reader flattens immediately, so this holds.
/// </summary>
public static class JsonRecordReader
{
    /// <summary>
    /// Enumerates the records in <paramref name="stream"/>: every top-level value is navigated to
    /// <paramref name="rootPath"/>, an array there fans out to one record per element, an object is a single
    /// record, and a scalar is no record at all.
    /// </summary>
    public static async IAsyncEnumerable<JsonElement> ReadRecordsAsync(
        Stream stream,
        string rootPath,
        string fileName,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var path = ParseRootPath(rootPath, fileName);

        using var tokens = new JsonTokenStream(stream, fileName);
        JsonDocument? current = null;
        try
        {
            while (await tokens.SkipToNextDocumentAsync(ct).ConfigureAwait(false))
            {
                tokens.BeginDocument();
                var target = await LocateAsync(tokens, path, ct).ConfigureAwait(false);

                if (target == RootTarget.Array)
                {
                    while (true)
                    {
                        var next = await tokens.PeekAsync(ct).ConfigureAwait(false);
                        if (next is null)
                        {
                            break;
                        }

                        if (next == JsonTokenType.EndArray)
                        {
                            await tokens.ReadAsync(ct).ConfigureAwait(false);
                            break;
                        }

                        var element = await tokens.ParseValueAsync(ct).ConfigureAwait(false);
                        if (element is null)
                        {
                            break;
                        }

                        current?.Dispose();
                        current = element;
                        yield return element.RootElement;
                    }
                }
                else if (target == RootTarget.Object)
                {
                    var record = await tokens.ParseValueAsync(ct).ConfigureAwait(false);
                    if (record is not null)
                    {
                        current?.Dispose();
                        current = record;
                        yield return record.RootElement;
                    }
                }
                else if (target == RootTarget.Scalar)
                {
                    // Addressable, but not a record: consume it so the next top-level value is reachable.
                    await tokens.SkipValueAsync(ct).ConfigureAwait(false);
                }

                await tokens.SkipToEndOfDocumentAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            current?.Dispose();
        }
    }

    /// <summary>
    /// Enumerates the top-level documents in <paramref name="stream"/> WITHOUT navigating a root path or
    /// expanding arrays into records: the whole parsed value per JSON document (one for a plain file, one per
    /// line for ndjson/jsonl). This is the raw view the record-anchor detector reasons over, before any grain is
    /// chosen. Like <see cref="ReadRecordsAsync"/>, each yielded element is valid only until the next is
    /// requested, so a consumer must finish with it (the detector copies out its small aggregate) before
    /// continuing. A whole document IS materialized here, which is what "the document as one value" means.
    /// </summary>
    public static async IAsyncEnumerable<JsonElement> ReadTopLevelDocumentsAsync(
        Stream stream,
        string fileName,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var tokens = new JsonTokenStream(stream, fileName);
        JsonDocument? current = null;
        try
        {
            while (await tokens.SkipToNextDocumentAsync(ct).ConfigureAwait(false))
            {
                tokens.BeginDocument();
                var document = await tokens.ParseValueAsync(ct).ConfigureAwait(false);
                if (document is null)
                {
                    break;
                }

                current?.Dispose();
                current = document;
                yield return document.RootElement;
            }
        }
        finally
        {
            current?.Dispose();
        }
    }

    /// <summary>What the root path resolved to inside the current top-level value.</summary>
    private enum RootTarget
    {
        /// <summary>The path does not exist in this value; nothing to read.</summary>
        NotFound,

        /// <summary>An array: its elements are the records, streamed one at a time.</summary>
        Array,

        /// <summary>An object: the single record.</summary>
        Object,

        /// <summary>A scalar: addressable, but not a record.</summary>
        Scalar,
    }

    /// <summary>One root-path step: an optional property name followed by any number of <c>[index]</c> hops.</summary>
    private sealed record PathStep(string Key, IReadOnlyList<int> Indices);

    /// <summary>
    /// Navigates the current top-level value to the root path and reports what is there, leaving the stream
    /// positioned so the target can be read: BEFORE the value for an object or scalar, and just INSIDE an
    /// array (its opening bracket consumed) so elements can be streamed. Navigation walks and skips; nothing
    /// on the way to the target is materialized.
    /// </summary>
    private static async ValueTask<RootTarget> LocateAsync(JsonTokenStream tokens, IReadOnlyList<PathStep> path, CancellationToken ct)
    {
        foreach (var step in path)
        {
            if (step.Key.Length > 0)
            {
                if (await tokens.PeekAsync(ct).ConfigureAwait(false) != JsonTokenType.StartObject)
                {
                    return RootTarget.NotFound;
                }

                await tokens.ReadAsync(ct).ConfigureAwait(false);
                if (!await SeekPropertyAsync(tokens, step.Key, ct).ConfigureAwait(false))
                {
                    return RootTarget.NotFound;
                }
            }

            foreach (var index in step.Indices)
            {
                if (await tokens.PeekAsync(ct).ConfigureAwait(false) != JsonTokenType.StartArray)
                {
                    return RootTarget.NotFound;
                }

                await tokens.ReadAsync(ct).ConfigureAwait(false);
                for (var i = 0; i < index; i++)
                {
                    var element = await tokens.PeekAsync(ct).ConfigureAwait(false);
                    if (element is null or JsonTokenType.EndArray)
                    {
                        return RootTarget.NotFound;
                    }

                    await tokens.SkipValueAsync(ct).ConfigureAwait(false);
                }

                if (await tokens.PeekAsync(ct).ConfigureAwait(false) is null or JsonTokenType.EndArray)
                {
                    return RootTarget.NotFound;
                }
            }
        }

        switch (await tokens.PeekAsync(ct).ConfigureAwait(false))
        {
            case JsonTokenType.StartArray:
                // Step inside so the elements stream out one at a time instead of the array being one value.
                await tokens.ReadAsync(ct).ConfigureAwait(false);
                return RootTarget.Array;

            case JsonTokenType.StartObject:
                return RootTarget.Object;

            case null:
                return RootTarget.NotFound;

            default:
                return RootTarget.Scalar;
        }
    }

    /// <summary>
    /// Advances to the named property's value inside the object the stream just entered, skipping the
    /// properties (and whole subtrees) before it. False when the object ends without the property.
    /// </summary>
    private static async ValueTask<bool> SeekPropertyAsync(JsonTokenStream tokens, string key, CancellationToken ct)
    {
        while (true)
        {
            var token = await tokens.PeekAsync(ct).ConfigureAwait(false);
            if (token is null)
            {
                return false;
            }

            if (token == JsonTokenType.EndObject)
            {
                await tokens.ReadAsync(ct).ConfigureAwait(false);
                return false;
            }

            var name = await tokens.ReadPropertyNameAsync(ct).ConfigureAwait(false);
            if (string.Equals(name, key, StringComparison.Ordinal))
            {
                return true;
            }

            await tokens.SkipValueAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Splits the root path (e.g. <c>$.data.records</c>, with optional <c>[index]</c> steps) into its steps.</summary>
    private static IReadOnlyList<PathStep> ParseRootPath(string rootPath, string fileName)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || rootPath == "$")
        {
            return [];
        }

        var trimmed = rootPath.StartsWith("$.", StringComparison.Ordinal)
            ? rootPath[2..]
            : rootPath.StartsWith('$') ? rootPath[1..] : rootPath;

        var steps = new List<PathStep>();
        foreach (var segment in trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = segment;
            IReadOnlyList<int> indices = [];

            var bracket = key.IndexOf('[', StringComparison.Ordinal);
            if (bracket >= 0)
            {
                indices = ParseIndices(key[bracket..], rootPath, fileName);
                key = key[..bracket];
            }

            steps.Add(new PathStep(key, indices));
        }

        return steps;
    }

    private static int[] ParseIndices(string brackets, string rootPath, string fileName)
    {
        var indices = new List<int>();
        var i = 0;
        while (i < brackets.Length)
        {
            if (brackets[i] != '[')
            {
                throw new SqlFlowException($"Invalid rootPath '{rootPath}' for '{fileName}': expected '[' at '{brackets[i..]}'.");
            }

            var close = brackets.IndexOf(']', i);
            if (close < 0)
            {
                throw new SqlFlowException($"Invalid rootPath '{rootPath}' for '{fileName}': missing ']'.");
            }

            var token = brackets[(i + 1)..close];
            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || index < 0)
            {
                throw new SqlFlowException($"Invalid rootPath index '[{token}]' in '{rootPath}' for '{fileName}'.");
            }

            indices.Add(index);
            i = close + 1;
        }

        return [.. indices];
    }
}
