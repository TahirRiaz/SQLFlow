using System.Text;
using SqlFlow.Core;
using SqlFlow.Core.Acquire;

namespace SqlFlow.Acquire.Runtime.Protection;

/// <summary>
/// The CSV adapter for landing-time protection. The rule path is the HEADER COLUMN NAME (an optional leading
/// <c>$.</c> is tolerated); the first line must be a header. Parsing is RFC-4180 quote-aware, and untouched fields
/// keep their raw bytes (original quoting included); only transformed fields are re-encoded, quoted when they
/// contain the delimiter, a quote, or a newline. <c>remove</c> blanks the field (dropping a whole column would
/// reshape the file's schema, which a protection rule must not do silently). The delimiter comes from a rule's
/// <c>delimiter</c> param when set, otherwise it is detected from the header among <c>; , tab |</c>. A rule naming
/// a column the header lacks is a configuration error: silently landing the file would mean the requested
/// protection did not happen.
/// </summary>
internal static class CsvProtection
{
    private static readonly char[] DelimiterCandidates = [';', ',', '\t', '|'];

    public static byte[] Apply(
        ReadOnlyMemory<byte> content,
        IReadOnlyList<AcquireProtectRule> rules,
        Func<string, AcquireProtectRule, (string? Result, bool Numeric)> transform)
    {
        var text = Encoding.UTF8.GetString(content.Span);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = SplitLines(text);
        if (lines.Count == 0)
        {
            return content.ToArray();
        }

        var delimiter = ResolveDelimiter(rules, lines[0]);
        var header = ParseFields(lines[0], delimiter);
        var columns = ResolveColumns(rules, header);

        var builder = new StringBuilder(text.Length);
        builder.Append(lines[0]);
        for (var i = 1; i < lines.Count; i++)
        {
            builder.Append(newline);
            var line = lines[i];
            if (line.Length == 0)
            {
                continue;
            }

            var fields = ParseFields(line, delimiter);
            foreach (var (rule, index) in columns)
            {
                if (index < fields.Count)
                {
                    var (result, _) = transform(fields[index].Value, rule);
                    fields[index] = new CsvField(result ?? string.Empty, Raw: null);
                }
            }

            for (var f = 0; f < fields.Count; f++)
            {
                if (f > 0)
                {
                    builder.Append(delimiter);
                }

                builder.Append(fields[f].Raw ?? Encode(fields[f].Value, delimiter));
            }
        }

        if (text.EndsWith('\n'))
        {
            builder.Append(newline);
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static char ResolveDelimiter(IReadOnlyList<AcquireProtectRule> rules, string headerLine)
    {
        foreach (var rule in rules)
        {
            if (rule.Params.TryGetValue("delimiter", out var d) && d.Length > 0)
            {
                return d == "\\t" ? '\t' : d[0];
            }
        }

        var best = DelimiterCandidates[0];
        var bestCount = -1;
        foreach (var candidate in DelimiterCandidates)
        {
            var count = headerLine.Count(c => c == candidate);
            if (count > bestCount)
            {
                best = candidate;
                bestCount = count;
            }
        }

        return best;
    }

    private static List<(AcquireProtectRule Rule, int Index)> ResolveColumns(
        IReadOnlyList<AcquireProtectRule> rules, IReadOnlyList<CsvField> header)
    {
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Count; i++)
        {
            byName[header[i].Value.Trim()] = i;
        }

        var resolved = new List<(AcquireProtectRule, int)>(rules.Count);
        foreach (var rule in rules)
        {
            var column = rule.Path.Trim();
            if (column.StartsWith("$.", StringComparison.Ordinal))
            {
                column = column[2..];
            }
            else if (column.StartsWith('$'))
            {
                column = column[1..];
            }

            if (!byName.TryGetValue(column, out var index))
            {
                throw new SqlFlowException(
                    $"landing.protect names CSV column '{column}' but the payload header has no such column; the file will not be landed unprotected. Header: {string.Join(", ", header.Select(h => h.Value))}.");
            }

            resolved.Add((rule, index));
        }

        return resolved;
    }

    /// <summary>A parsed field: the decoded value, plus the raw slice when the field was not transformed (so
    /// untouched fields round-trip byte-for-byte, original quoting included).</summary>
    private readonly record struct CsvField(string Value, string? Raw);

    private static List<CsvField> ParseFields(string line, char delimiter)
    {
        var fields = new List<CsvField>();
        var value = new StringBuilder();
        var rawStart = 0;
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        value.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    value.Append(c);
                }

                continue;
            }

            if (c == '"')
            {
                inQuotes = true;
                continue;
            }

            if (c == delimiter)
            {
                fields.Add(new CsvField(value.ToString(), line[rawStart..i]));
                value.Clear();
                rawStart = i + 1;
                continue;
            }

            value.Append(c);
        }

        fields.Add(new CsvField(value.ToString(), line[rawStart..]));
        return fields;
    }

    private static string Encode(string value, char delimiter)
        => value.Contains(delimiter, StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal)
           || value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal)
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;

    private static List<string> SplitLines(string text)
    {
        // Quote-aware line split: a newline inside a quoted field is field content, not a record boundary.
        var lines = new List<string>();
        var start = 0;
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == '\n' && !inQuotes)
            {
                var end = i > start && text[i - 1] == '\r' ? i - 1 : i;
                lines.Add(text[start..end]);
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }
}
