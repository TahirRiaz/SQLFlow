using System.Globalization;
using System.Text;
using SqlFlow.Core;

namespace SqlFlow.Acquire.Runtime;

/// <summary>
/// Substitutes <c>{token}</c> placeholders in request paths, query values, headers, bodies, and landing paths from
/// a per-request context. Three token shapes, resolved in order:
/// <list type="number">
/// <item>a bare date/time format (<c>{yyyy}</c>, <c>{yyyyMMdd}</c>, <c>{yyyy-MM-dd}</c>, <c>{HH:mm:ss}</c>) is
/// formatted against the reference date;</item>
/// <item><c>{name:format}</c> formats a date-valued context variable (<c>{window.from:yyyy-MM-dd}</c>), with a
/// <c>utc:</c> prefix converting to UTC first (<c>{window.from:utc:yyyy-MM-ddTHH}</c>);</item>
/// <item><c>{expression:format}</c> formats a relative date computed from the reference date
/// (<c>{now-6mo:yyyy-MM-dd}</c>, <c>{startOfMonth:yyyy-MM-dd}</c>), for a request that needs a rolling boundary
/// without a date-window iteration to bind it;</item>
/// <item><c>{name}</c> substitutes a context variable verbatim (a string) or ISO-8601 (a date).</item>
/// </list>
/// A referenced variable that is not in the context is an authoring error, not a silent blank, so a broken template
/// fails the run with a message naming the token rather than sending a malformed request.
/// </summary>
public static class TemplateEngine
{
    /// <summary>Render a template, leaving substituted values unescaped (headers, bodies, JSON, base paths).</summary>
    public static string Render(string template, TemplateContext context)
        => Render(template, context, encode: false);

    /// <summary>Render a template, URL-escaping each substituted value (query values, path segments).</summary>
    public static string RenderEncoded(string template, TemplateContext context)
        => Render(template, context, encode: true);

    private static string Render(string template, TemplateContext context, bool encode)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(context);

        if (template.IndexOf('{', StringComparison.Ordinal) < 0)
        {
            return template;
        }

        var result = new StringBuilder(template.Length + 16);
        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];

            // A secret reference '${scheme:locator}' is not a template placeholder: emit it verbatim so the secret
            // resolver expands it afterwards. Without this, the leading '{' of the reference would be read as a token.
            if (c == '$' && i + 1 < template.Length && template[i + 1] == '{')
            {
                var secretEnd = template.IndexOf('}', i + 2);
                if (secretEnd >= 0)
                {
                    result.Append(template, i, secretEnd - i + 1);
                    i = secretEnd + 1;
                    continue;
                }
            }

            if (c == '{')
            {
                // An escaped literal brace: "{{" -> "{".
                if (i + 1 < template.Length && template[i + 1] == '{')
                {
                    result.Append('{');
                    i += 2;
                    continue;
                }

                var end = template.IndexOf('}', i + 1);
                if (end < 0)
                {
                    throw new SqlFlowException($"Unterminated '{{' in template '{template}'.");
                }

                var token = template[(i + 1)..end];
                var value = Resolve(token, context, template);
                result.Append(encode ? Uri.EscapeDataString(value) : value);
                i = end + 1;
                continue;
            }

            if (c == '}' && i + 1 < template.Length && template[i + 1] == '}')
            {
                result.Append('}');
                i += 2;
                continue;
            }

            result.Append(c);
            i++;
        }

        return result.ToString();
    }

    private static string Resolve(string token, TemplateContext context, string template)
    {
        if (token.Length == 0)
        {
            throw new SqlFlowException($"Empty '{{}}' placeholder in template '{template}'.");
        }

        // 1. A pure date/time format applied to the reference date.
        if (IsDateFormat(token))
        {
            return context.ReferenceDate.ToString(token, CultureInfo.InvariantCulture);
        }

        // 2. name:format for a date-valued variable.
        var colon = token.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0)
        {
            var name = token[..colon];
            var format = token[(colon + 1)..];
            if (context.TryGetDate(name, out var date))
            {
                // The 'utc:' prefix shifts the value to UTC before formatting, for an API whose window
                // parameter is expressed in UTC (e.g. ?end_time={window.from:utc:yyyy-MM-ddTHH}). Without it a
                // window built in local time renders local hours, which such an API reads as a different - and,
                // near now, a still-future - period.
                if (format.StartsWith("utc:", StringComparison.Ordinal))
                {
                    return date.ToUniversalTime().ToString(format[4..], CultureInfo.InvariantCulture);
                }

                // The pseudo-formats unix / unixms render the variable as an epoch count, for APIs whose window
                // parameters are unix timestamps (e.g. ?fromCreatedAt={window.from:unix}). Both are absolute
                // instants, so they need no UTC variant.
                return format switch
                {
                    "unix" => date.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                    "unixms" => date.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                    _ => date.ToString(format, CultureInfo.InvariantCulture),
                };
            }

            if (context.TryGetString(name, out var raw))
            {
                return raw;
            }

            // A relative date expression rather than a variable: {now-6mo:yyyy-MM-dd} lets a request that needs a
            // rolling boundary compute it inline, instead of forcing a date-window iteration whose only purpose
            // would be to bind one date - and which would fan the request out into one call per step.
            if (RelativeTime.TryResolveAnchor(name, context.ReferenceDate, out var relative))
            {
                return format.StartsWith("utc:", StringComparison.Ordinal)
                    ? relative.ToUniversalTime().ToString(format[4..], CultureInfo.InvariantCulture)
                    : format switch
                    {
                        "unix" => relative.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                        "unixms" => relative.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                        _ => relative.ToString(format, CultureInfo.InvariantCulture),
                    };
            }

            throw Missing(name, template, context);
        }

        // 3. A plain variable.
        if (context.TryGetDate(token, out var dateValue))
        {
            return dateValue.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
        }

        if (context.TryGetString(token, out var value))
        {
            return value;
        }

        throw Missing(token, template, context);
    }

    private static SqlFlowException Missing(string name, string template, TemplateContext context)
    {
        var known = context.VariableNames.Count == 0 ? "(none)" : string.Join(", ", context.VariableNames.OrderBy(n => n, StringComparer.Ordinal));
        return new SqlFlowException(
            $"Template '{template}' references unknown variable '{name}'. Available variables: {known}. " +
            "Bind it from an iteration, the incremental watermark, or a built-in date token.");
    }

    /// <summary>
    /// True when the token is composed only of .NET date/time format characters and carries at least one field
    /// specifier, so it can be applied to the reference date (e.g. <c>yyyy</c>, <c>yyyyMMdd</c>, <c>HH:mm:ss</c>).
    /// A token carrying any other letter (e.g. <c>operatorId</c>, <c>window.from</c>) is a variable, not a format.
    /// </summary>
    private static bool IsDateFormat(string token)
    {
        var hasField = false;
        foreach (var c in token)
        {
            switch (c)
            {
                case 'y' or 'M' or 'd' or 'H' or 'h' or 'm' or 's' or 'f' or 'F' or 't' or 'z' or 'K':
                    hasField = true;
                    break;
                case ':' or '/' or '-' or '.' or ' ' or 'T' or 'Z' or '\'':
                    break;
                default:
                    return false;
            }
        }

        return hasField;
    }
}

/// <summary>The variable bag a template renders against: string variables, date variables, and a reference date the
/// bare date tokens format. Immutable; the engine builds one per request from the iteration values and watermark.</summary>
public sealed class TemplateContext
{
    private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _dates = new(StringComparer.Ordinal);

    public TemplateContext(DateTimeOffset referenceDate)
    {
        ReferenceDate = referenceDate;
    }

    /// <summary>The date the bare <c>{yyyy}</c>-style tokens format (typically the window lower bound, else now).</summary>
    public DateTimeOffset ReferenceDate { get; private set; }

    public IReadOnlyCollection<string> VariableNames => _strings.Keys.Concat(_dates.Keys).ToList();

    public TemplateContext WithString(string name, string value)
    {
        _strings[name] = value;
        return this;
    }

    public TemplateContext WithDate(string name, DateTimeOffset value)
    {
        _dates[name] = value;
        return this;
    }

    public TemplateContext WithReferenceDate(DateTimeOffset value)
    {
        ReferenceDate = value;
        return this;
    }

    /// <summary>A deep copy, so an inner iteration can add variables without mutating the outer context.</summary>
    public TemplateContext Clone()
    {
        var copy = new TemplateContext(ReferenceDate);
        foreach (var (k, v) in _strings)
        {
            copy._strings[k] = v;
        }

        foreach (var (k, v) in _dates)
        {
            copy._dates[k] = v;
        }

        return copy;
    }

    public bool TryGetString(string name, out string value) => _strings.TryGetValue(name, out value!);

    public bool TryGetDate(string name, out DateTimeOffset value) => _dates.TryGetValue(name, out value);
}
