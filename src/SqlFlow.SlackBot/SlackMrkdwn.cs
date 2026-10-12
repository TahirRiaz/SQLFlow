using System.Collections.Frozen;
using System.Text;
using System.Text.RegularExpressions;

namespace SqlFlow.SlackBot;

/// <summary>
/// Converts common Markdown habits to Slack mrkdwn. The agent is instructed to emit mrkdwn
/// directly, so this is a safety net for the constructs models fall back to anyway:
/// <c>**bold**</c> becomes <c>*bold*</c>, <c>[text](url)</c> becomes <c>&lt;url|text&gt;</c>, and
/// <c># headings</c> become bold lines. Fenced code blocks pass through untouched, except that an opening
/// fence's language tag (<c>```sql</c>) is dropped: Slack does not highlight code, so it would show the tag as
/// the block's first line. Only a recognised language name is a tag; any other text after the backticks
/// (<c>```SELECT</c>) is the block's first line of code and is kept. A <c>~~~</c> fence, which Slack does not render as
/// code, is written with backticks, and a block opened and closed on one line stays one line of code.
/// </summary>
public static partial class SlackMrkdwn
{
    /// <summary>Slack rejects messages past 40k characters; leave headroom for the truncation note.</summary>
    private const int MaxLength = 39_000;

    private const string Fence = "```";

    private const string TruncationNote = "_(answer truncated for Slack's message size limit)_";

    /// <summary>The info strings a model writes after an opening fence to name the block's language. Compared without
    /// regard to case, so <c>```SQL</c> is a tag while <c>```SELECT</c> is code.</summary>
    private static readonly FrozenSet<string> LanguageTags = new[]
    {
        "sql", "tsql", "t-sql", "mssql", "plsql", "mysql", "postgres", "postgresql", "pgsql", "sqlite",
        "dax", "m", "powerquery", "kusto", "kql",
        "json", "jsonc", "yaml", "yml", "xml", "html", "css", "csv", "ini", "toml", "markdown", "md",
        "text", "txt", "plain", "plaintext", "console", "output", "log",
        "bash", "sh", "shell", "zsh", "powershell", "pwsh", "ps1", "cmd", "bat",
        "python", "py", "csharp", "cs", "c#", "fsharp", "javascript", "js", "typescript", "ts", "jsx", "tsx",
        "java", "kotlin", "scala", "go", "rust", "rs", "ruby", "rb", "php", "c", "cpp", "c++",
        "diff", "graphql", "dockerfile", "makefile", "mermaid", "http",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"\*\*(.+?)\*\*", RegexOptions.Singleline)]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\((https?://[^)\s]+)\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"^#{1,6}\s+(.+)$")]
    private static partial Regex HeadingRegex();

    /// <summary>A fence line: its indentation, the fence itself (three or more backticks, or three or more tildes), and
    /// what follows it.</summary>
    [GeneratedRegex(@"^(\s*)(`{3,}|~{3,})(.*)$")]
    private static partial Regex FenceLineRegex();

    public static string FromMarkdown(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = new List<string>();
        var inFence = false;

        // The fence that opened the block being read: a block closes only on a fence of the same character, at least as
        // long, as Markdown does.
        var opening = string.Empty;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var fence = FenceLineRegex().Match(line);
            if (inFence)
            {
                if (fence.Success && ClosesBlock(fence, opening))
                {
                    inFence = false;
                    lines.Add(fence.Groups[1].Value + Fence);
                    continue;
                }

                lines.Add(line);
                continue;
            }

            if (fence.Success)
            {
                var indent = fence.Groups[1].Value;
                var marker = fence.Groups[2].Value;
                var rest = fence.Groups[3].Value;

                // A block opened and closed on one line (```SELECT 1```) is one line of code, not an opening fence.
                var tail = rest.TrimEnd();
                if (tail.EndsWith(marker[..3], StringComparison.Ordinal))
                {
                    lines.Add(indent + Fence + tail[..^3].TrimEnd(marker[0]) + Fence);
                    continue;
                }

                // The info string's first word names the language, as Markdown reads it.
                inFence = true;
                opening = marker;
                var language = rest.Trim().Split(' ', 2)[0];
                lines.Add(LanguageTags.Contains(language) ? indent + Fence : indent + Fence + rest);
                continue;
            }

            // A heading is made bold whole, so bold inside it is unwrapped first rather than doubled.
            line = LinkRegex().Replace(line, "<$2|$1>");
            var heading = HeadingRegex().Match(line);
            line = heading.Success
                ? "*" + Unwrapped(BoldRegex().Replace(heading.Groups[1].Value, "$1")) + "*"
                : BoldRegex().Replace(line, "*$1*");
            lines.Add(line);
        }

        var converted = string.Join('\n', lines).TrimEnd('\n');
        if (converted.Length <= MaxLength)
        {
            return converted;
        }

        var kept = converted[..MaxLength];

        // A cut inside a code block would put the note inside it, as code: close the block first.
        return (OpensMoreThanItCloses(kept) ? kept + "\n" + Fence : kept) + "\n" + TruncationNote;
    }

    /// <summary>Whether <paramref name="fence"/> closes a block <paramref name="opening"/> began: the same character, at
    /// least as many of it, and nothing after it.</summary>
    private static bool ClosesBlock(Match fence, string opening)
        => fence.Groups[2].Value[0] == opening[0]
            && fence.Groups[2].Value.Length >= opening.Length
            && string.IsNullOrWhiteSpace(fence.Groups[3].Value);

    /// <summary>A heading's text without one pair of single asterisks around the whole of it, so <c>## *Summary*</c>
    /// does not become <c>**Summary**</c>.</summary>
    private static string Unwrapped(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length > 2 && trimmed[0] == '*' && trimmed[^1] == '*' ? trimmed[1..^1] : trimmed;
    }

    /// <summary>Whether <paramref name="converted"/> (already converted, so a block opens on a <c>```</c> line, closes on a
    /// bare <c>```</c> line, and a one-line block is written <c>```code```</c>) ends inside a code block.</summary>
    private static bool OpensMoreThanItCloses(string converted)
    {
        var open = false;
        foreach (var line in converted.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(Fence, StringComparison.Ordinal))
            {
                continue;
            }

            if (open)
            {
                open = trimmed != Fence;
                continue;
            }

            var oneLine = trimmed.Length > Fence.Length && trimmed.EndsWith(Fence, StringComparison.Ordinal);
            open = !oneLine;
        }

        return open;
    }
}
