using System.Text;
using System.Text.RegularExpressions;

namespace SqlFlow.Core.Acquire;

/// <summary>
/// Derives an acquisition's resume point from what is ALREADY LANDED in the raw zone (<c>incremental.source: lake</c>).
/// This is the acquisition's equivalent of the way an ingestion flow probes MAX(column) on its target table: the
/// durable state lives in the data, not in a log, so the resume point survives a redeploy, a different worker replica,
/// and a laptop-versus-estate split, and deleting landed files genuinely re-opens the window instead of leaving the
/// flow permanently convinced it already fetched them.
///
/// The landing <c>pathTemplate</c> is the contract in both directions: the engine renders it to NAME each file, and
/// this reader turns the same template into an anchored regex to READ the watermark back out of those names. A
/// template must therefore carry exactly one placeholder, or name which one holds the watermark via
/// <c>incremental.column</c>; anything ambiguous is an authoring error raised before a single request is sent, never a
/// silent fall back to a full re-walk.
/// </summary>
public static class LakeWatermarkReader
{
    /// <summary>Regex safety valve: templates are authored, not user input, but a pathological pattern must not turn a
    /// resume into a hang. A per-name match cannot exceed this.</summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The maximum watermark across <paramref name="landedNames"/>, or null when nothing has landed yet (a first run,
    /// which then falls back to the configured seed). Names that do not match the template are ignored: a raw zone
    /// legitimately holds header sidecars, an archive subfolder, and files from an earlier naming scheme, and none of
    /// those may corrupt the resume point.
    /// </summary>
    /// <param name="pathTemplate">The item's landing <c>pathTemplate</c>, e.g. <c>history/report_{header.x-id}</c>.</param>
    /// <param name="landedNames">Names relative to the landing base, '/'-separated.</param>
    /// <param name="token">The placeholder holding the watermark (<c>incremental.column</c>); null selects the single
    /// placeholder when the template has exactly one.</param>
    public static string? Read(string pathTemplate, IEnumerable<string> landedNames, string? token = null)
        => Read(Compile(pathTemplate, token), landedNames);

    /// <summary>Reads the maximum watermark using an already-validated template, so a caller that compiled once (to
    /// report which placeholder it selected) does not rebuild the regex per run.</summary>
    public static string? Read(CompiledTemplate pattern, IEnumerable<string> landedNames)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(landedNames);

        string? max = null;
        foreach (var name in landedNames)
        {
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            Match match;
            try
            {
                match = pattern.Regex.Match(name);
            }
            catch (RegexMatchTimeoutException)
            {
                // One pathological name must not fail the run: it simply contributes no candidate.
                continue;
            }

            if (match.Success && match.Groups[pattern.Group] is { Success: true, Value: { Length: > 0 } value })
            {
                max = WatermarkOrder.Max(max, value);
            }
        }

        return max;
    }

    /// <summary>
    /// Validates that <paramref name="pathTemplate"/> can yield a watermark and compiles the matcher. Called at flow
    /// load so a misconfigured lake resume fails validation rather than at 04:30 on a schedule.
    /// </summary>
    public static CompiledTemplate Compile(string pathTemplate, string? token = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathTemplate);

        var (regex, placeholders) = Build(pathTemplate);
        if (placeholders.Count == 0)
        {
            throw new SqlFlowException(
                $"incremental.source 'lake' needs the watermark to appear in the landing path, but pathTemplate " +
                $"'{pathTemplate}' has no '{{placeholder}}'. Name each landed file after the value the next run " +
                "resumes from (e.g. 'history/report_{header.x-report-id}'), or use a different incremental source.");
        }

        var selected = token is { Length: > 0 }
            ? placeholders.FindIndex(p => string.Equals(p, token, StringComparison.Ordinal))
            : placeholders.Count == 1 ? 0 : -1;

        if (selected < 0)
        {
            throw new SqlFlowException(token is { Length: > 0 }
                ? $"incremental.column '{token}' is not a placeholder of landing pathTemplate '{pathTemplate}'. "
                  + $"Available: {string.Join(", ", placeholders.Select(p => $"'{p}'"))}."
                : $"incremental.source 'lake' cannot tell which part of pathTemplate '{pathTemplate}' is the "
                  + $"watermark: it has {placeholders.Count} placeholders ({string.Join(", ", placeholders.Select(p => $"'{p}'"))}). "
                  + "Set 'incremental.column' to the one that carries it.");
        }

        return new CompiledTemplate(regex, GroupName(selected), placeholders[selected], placeholders);
    }

    /// <summary>
    /// Turns a landing template into an anchored regex, mirroring <c>TemplateEngine</c>'s token syntax exactly:
    /// <c>{{</c>/<c>}}</c> are literal braces, <c>${scheme:locator}</c> is a secret reference rather than a
    /// placeholder, and everything else inside braces is one placeholder. Literal text is escaped, each placeholder
    /// becomes a lazy capture bounded to a single path segment, and the pattern accepts the extension (and any
    /// collision discriminator or <c>.gz</c>) the landing pipeline appends after the rendered template.
    /// </summary>
    private static (Regex Regex, List<string> Placeholders) Build(string template)
    {
        var pattern = new StringBuilder("^");
        var placeholders = new List<string>();
        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];

            if (c == '$' && i + 1 < template.Length && template[i + 1] == '{')
            {
                var secretEnd = template.IndexOf('}', i + 2);
                if (secretEnd >= 0)
                {
                    // A secret reference is resolved to a literal before any file is named, and its value is not
                    // knowable here, so it matches as an opaque single-segment run rather than as a placeholder.
                    pattern.Append("[^/]*");
                    i = secretEnd + 1;
                    continue;
                }
            }

            if (c == '{')
            {
                if (i + 1 < template.Length && template[i + 1] == '{')
                {
                    pattern.Append(Regex.Escape("{"));
                    i += 2;
                    continue;
                }

                var end = template.IndexOf('}', i + 1);
                if (end < 0)
                {
                    throw new SqlFlowException($"Unterminated '{{' in landing pathTemplate '{template}'.");
                }

                var name = template[(i + 1)..end];
                if (name.Length == 0)
                {
                    throw new SqlFlowException($"Empty '{{}}' placeholder in landing pathTemplate '{template}'.");
                }

                pattern.Append("(?<").Append(GroupName(placeholders.Count)).Append(">[^/]+?)");
                placeholders.Add(name);
                i = end + 1;
                continue;
            }

            if (c == '}' && i + 1 < template.Length && template[i + 1] == '}')
            {
                pattern.Append(Regex.Escape("}"));
                i += 2;
                continue;
            }

            pattern.Append(Regex.Escape(c.ToString()));
            i++;
        }

        // The pipeline appends '.<extension>', optionally '.<discriminator>' before it on a name collision, and
        // '.gz' when compressed. Requiring at least one dotted suffix is what stops a placeholder from swallowing the
        // extension: 'report_508266.xlsx' yields 508266, not '508266.xlsx'.
        pattern.Append("\\.[^/]+$");
        return (new Regex(pattern.ToString(), RegexOptions.CultureInvariant, MatchTimeout), placeholders);
    }

    /// <summary>Placeholder names are arbitrary template tokens (dots, dashes), which are not legal regex group names,
    /// so groups are numbered positionally instead.</summary>
    private static string GroupName(int index) => $"w{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>A validated template matcher: the anchored regex, the capture group holding the watermark, the
    /// placeholder that group came from, and every placeholder in declaration order (for diagnostics).</summary>
    public sealed record CompiledTemplate(Regex Regex, string Group, string Selected, IReadOnlyList<string> Placeholders);
}
