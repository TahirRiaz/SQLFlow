using System.Text;

namespace SqlFlow.Core.Ingestion;

/// <summary>
/// Text helpers for parsing SQLFlow identifier lists and bracketed multipart names, matching the legacy
/// comma/period splitting that tolerates bracket-quoted identifiers (an inner ']' is escaped as ']]').
/// </summary>
public static class IngestionText
{
    /// <summary>
    /// Splits <paramref name="value"/> on <paramref name="delimiter"/> but not inside a [bracketed]
    /// identifier. Bracket characters are kept on each token; use <see cref="Unbracket"/> to strip them.
    /// </summary>
    public static List<string> SplitRespectingBrackets(string value, char delimiter)
    {
        var parts = new List<string>();
        var token = new StringBuilder();
        var inBracket = false;

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (!inBracket)
            {
                if (c == delimiter)
                {
                    parts.Add(token.ToString());
                    token.Clear();
                }
                else
                {
                    if (c == '[')
                    {
                        inBracket = true;
                    }

                    token.Append(c);
                }
            }
            else if (c == ']')
            {
                if (i + 1 < value.Length && value[i + 1] == ']')
                {
                    token.Append("]]");
                    i++;
                }
                else
                {
                    inBracket = false;
                    token.Append(c);
                }
            }
            else
            {
                token.Append(c);
            }
        }

        parts.Add(token.ToString());
        return parts;
    }

    /// <summary>Trims a token and, if it is a single [bracketed] identifier, removes the brackets and
    /// unescapes ']]' to ']'.</summary>
    public static string Unbracket(string token)
    {
        var trimmed = token.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']'
            ? trimmed[1..^1].Replace("]]", "]", StringComparison.Ordinal)
            : trimmed;
    }

    /// <summary>
    /// Parses a comma-separated identifier list (legacy KeyColumns / IgnoreColumns / etc.): split on commas
    /// outside brackets, unbracket and trim each item, and drop empties. Null or blank yields an empty list.
    /// </summary>
    public static IReadOnlyList<string> ParseList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var result = new List<string>();
        foreach (var raw in SplitRespectingBrackets(value, ','))
        {
            var item = Unbracket(raw);
            if (item.Length > 0)
            {
                result.Add(item);
            }
        }

        return result;
    }
}
