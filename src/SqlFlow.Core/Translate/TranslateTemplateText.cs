namespace SqlFlow.Core.Translate;

/// <summary>
/// The tokenizer of the translate dialect's templated strings: literal text interleaved with <c>{Column}</c>
/// tokens. <c>{{</c> and <c>}}</c> escape literal braces, and a <c>${...}</c> sequence passes through verbatim
/// (it is a secret reference for the secret resolver, exactly as in the acquisition template engine). Shared
/// between the YAML loader (which classifies scalars and validates token syntax at parse time) and the runtime
/// renderer, so a template that validates is guaranteed to tokenize identically at run time.
/// </summary>
public static class TranslateTemplateText
{
    /// <summary>One parsed segment: literal text, or a column token to substitute.</summary>
    public readonly record struct Segment(string Text, bool IsToken);

    /// <summary>
    /// Parses a templated string into its segments. Throws <see cref="SqlFlowException"/> on an unclosed token or
    /// an empty token name; a lone <c>}</c> is literal (only <c>{</c> opens a token, so it needs no escape outside
    /// the <c>}}</c> pair).
    /// </summary>
    public static IReadOnlyList<Segment> Parse(string template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var segments = new List<Segment>();
        var literal = new System.Text.StringBuilder();

        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];

            if (c == '{')
            {
                if (i + 1 < template.Length && template[i + 1] == '{')
                {
                    literal.Append('{');
                    i++;
                    continue;
                }

                var close = template.IndexOf('}', i + 1);
                if (close < 0)
                {
                    throw new SqlFlowException($"Template '{template}' has an unclosed '{{' at position {i}.");
                }

                var name = template[(i + 1)..close].Trim();
                if (name.Length == 0)
                {
                    throw new SqlFlowException($"Template '{template}' has an empty '{{}}' token at position {i}.");
                }

                if (literal.Length > 0)
                {
                    segments.Add(new Segment(literal.ToString(), IsToken: false));
                    literal.Clear();
                }

                segments.Add(new Segment(name, IsToken: true));
                i = close;
                continue;
            }

            if (c == '}' && i + 1 < template.Length && template[i + 1] == '}')
            {
                literal.Append('}');
                i++;
                continue;
            }

            if (c == '$' && i + 1 < template.Length && template[i + 1] == '{')
            {
                // A ${...} secret reference passes through verbatim, closing brace included, so the secret
                // resolver (not the row scope) expands it later.
                var close = template.IndexOf('}', i + 2);
                if (close < 0)
                {
                    throw new SqlFlowException($"Template '{template}' has an unclosed '${{' at position {i}.");
                }

                literal.Append(template, i, close - i + 1);
                i = close;
                continue;
            }

            literal.Append(c);
        }

        if (literal.Length > 0)
        {
            segments.Add(new Segment(literal.ToString(), IsToken: false));
        }

        return segments;
    }

    /// <summary>True when the string is exactly one <c>{Column}</c> token and nothing else: the typed-passthrough
    /// form, where the leaf emits the column's native JSON value rather than a string.</summary>
    public static bool IsSingleToken(string template, out string column)
    {
        var segments = Parse(template);
        if (segments.Count == 1 && segments[0].IsToken)
        {
            column = segments[0].Text;
            return true;
        }

        column = string.Empty;
        return false;
    }

    /// <summary>True when the string carries at least one token (so it renders per row rather than as a constant).</summary>
    public static bool HasTokens(string template) => Parse(template).Any(s => s.IsToken);

    /// <summary>The distinct column names a template references, for validation and lineage.</summary>
    public static IReadOnlyList<string> Columns(string template)
        => Parse(template).Where(s => s.IsToken).Select(s => s.Text).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
