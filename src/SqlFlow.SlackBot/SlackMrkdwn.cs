using System.Text;
using System.Text.RegularExpressions;

namespace SqlFlow.SlackBot;

/// <summary>
/// Converts common Markdown habits to Slack mrkdwn. The agent is instructed to emit mrkdwn
/// directly, so this is a safety net for the constructs models fall back to anyway:
/// <c>**bold**</c> becomes <c>*bold*</c>, <c>[text](url)</c> becomes <c>&lt;url|text&gt;</c>, and
/// <c># headings</c> become bold lines. Fenced code blocks pass through untouched.
/// </summary>
public static partial class SlackMrkdwn
{
    /// <summary>Slack rejects messages past 40k characters; leave headroom for the truncation note.</summary>
    private const int MaxLength = 39_000;

    [GeneratedRegex(@"\*\*(.+?)\*\*", RegexOptions.Singleline)]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\((https?://[^)\s]+)\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"^#{1,6}\s+(.+)$")]
    private static partial Regex HeadingRegex();

    public static string FromMarkdown(string text)
    {
        var result = new StringBuilder(text.Length);
        var inFence = false;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                result.Append(line).Append('\n');
                continue;
            }
            if (inFence)
            {
                result.Append(line).Append('\n');
                continue;
            }
            line = LinkRegex().Replace(line, "<$2|$1>");
            line = BoldRegex().Replace(line, "*$1*");
            line = HeadingRegex().Replace(line, "*$1*");
            result.Append(line).Append('\n');
        }

        var converted = result.ToString().TrimEnd('\n');
        if (converted.Length > MaxLength)
        {
            converted = converted[..MaxLength] + "\n_(answer truncated for Slack's message size limit)_";
        }
        return converted;
    }
}
