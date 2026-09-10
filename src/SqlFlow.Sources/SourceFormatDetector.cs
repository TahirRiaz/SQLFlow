using System.Text;
using System.Text.RegularExpressions;

namespace SqlFlow.Sources;

/// <summary>The format a detector settled on, how sure it is, and the human-readable evidence for that call.
/// <see cref="Type"/> is a reader source type ("json", "ndjson", "xml", "csv", "xls", "xlsx", "parquet") or null
/// when nothing recognizable was found. <see cref="CsvDelimiter"/> is the detected delimiter for a delimited text
/// file (null means the default comma).</summary>
public sealed record SourceFormatDetection(
    string? Type, string Confidence, IReadOnlyList<string> Evidence, string? CsvDelimiter = null);

/// <summary>
/// A pure, no-I/O format detector: magic bytes first, then text-shape heuristics, then the file extension as a
/// last resort - the same priority order DeltaForge's content sniffer uses, mapped onto SQLFlow's reader source
/// types. The caller supplies a head sample (SQLFlow reads 64 KiB) and an optional file-name hint; detection never
/// throws and returns a confidence the caller can gate on (a low-confidence guess should not auto-register).
/// </summary>
public static class SourceFormatDetector
{
    /// <summary>The maximum head sample the detector needs; larger inputs are simply ignored past this.</summary>
    public const int HeadSampleBytes = 64 * 1024;

    // Candidate delimiters, most-specific first, so a file that is consistent under several (rare) picks the least
    // ambiguous. Comma is the default and yields a null CsvDelimiter (the reader's own default).
    private static readonly (char Ch, string? Option)[] CsvDelimiters =
        [('\t', "\t"), (';', ";"), ('|', "|"), (',', null)];

    private static readonly Regex NdjsonBreak = new(@"\}\s*[\r\n]+\s*\{", RegexOptions.Compiled);

    /// <summary>Maps a file name's extension to a reader source type, or null when the extension is unknown.</summary>
    public static string? TypeFromExtension(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "json" => "json",
            "ndjson" => "ndjson",
            "jsonl" => "jsonl",
            "xml" => "xml",
            "csv" => "csv",
            "tsv" or "tab" => "csv",
            "txt" => "csv",
            "xlsx" or "xlsm" => "xlsx",
            "xls" => "xls",
            "parquet" or "parq" or "prq" => "parquet",
            _ => null,
        };
    }

    /// <summary>
    /// Detects the delimiter of a CSV head sample once the format is already known to be delimited text (a format the
    /// caller pinned explicitly, or resolved from a <c>.csv</c> extension, where the content detector never ran).
    /// Returns the reader delimiter option (null means the default comma) and the human-readable evidence. This is
    /// what stops a semicolon/tab/pipe file from being read as a single comma-delimited column: the delimiter is
    /// profiled from the data itself, not assumed from the extension.
    /// </summary>
    public static (string? Option, string Evidence) DetectCsvDelimiter(ReadOnlySpan<byte> head)
    {
        var (text, _) = DecodeHead(head);
        var (delimiter, option, consistency, count) = ProfileDelimited(text);
        if (count == 0)
        {
            return (null, "no delimiter found in the sample; reading as a single column");
        }

        var name = delimiter == '\t' ? "tab" : $"'{delimiter}'";
        return (option,
            $"delimiter detected: {name} ({count} field(s), {(int)(consistency * 100)}% consistent across sampled lines)");
    }

    /// <summary>
    /// Detects the format of a head sample. Magic bytes win outright; failing that, the text shape (XML declaration,
    /// JSON braces, delimited-text profiling) decides; failing that, the file-name extension is the low-confidence
    /// fallback. Returns <see cref="SourceFormatDetection.Type"/> null only when nothing recognizable was found.
    /// </summary>
    public static SourceFormatDetection FromContent(ReadOnlySpan<byte> head, string? fileNameHint)
    {
        var extType = fileNameHint is null ? null : TypeFromExtension(fileNameHint);

        // 1. Magic bytes - unambiguous binary containers.
        if (head.Length >= 4)
        {
            if (head[0] == (byte)'P' && head[1] == (byte)'A' && head[2] == (byte)'R' && head[3] == (byte)'1')
            {
                return new SourceFormatDetection("parquet", "high", ["magic bytes 'PAR1' (Apache Parquet)"]);
            }

            if (head[0] == 0x50 && head[1] == 0x4B && head[2] == 0x03 && head[3] == 0x04)
            {
                // A ZIP container: modern Excel (.xlsx) is the only OOXML source SQLFlow reads.
                return new SourceFormatDetection("xlsx", "high", ["ZIP/OOXML container (PK\\x03\\x04), read as .xlsx"]);
            }

            if (head[0] == 0xD0 && head[1] == 0xCF && head[2] == 0x11 && head[3] == 0xE0)
            {
                return new SourceFormatDetection("xls", "high", ["OLE2 compound file (legacy Excel .xls)"]);
            }
        }

        if (head.Length >= 2 && head[0] == 0x1F && head[1] == 0x8B)
        {
            // gzip: no reader decompresses on the fly, so surface it rather than guess a text shape from bytes.
            return new SourceFormatDetection(extType, extType is null ? "low" : "low",
                ["gzip-compressed stream (0x1F8B); decompress the files first, or set the format explicitly"]);
        }

        var (text, encodingNote) = DecodeHead(head);
        var trimmed = text.AsSpan().TrimStart();
        if (trimmed.IsEmpty)
        {
            return new SourceFormatDetection(extType, "low",
                extType is null ? ["empty or whitespace-only sample"] : [$"empty sample; fell back to extension ({extType})"]);
        }

        var evidence = new List<string>();
        if (encodingNote is not null)
        {
            evidence.Add(encodingNote);
        }

        var first = trimmed[0];

        // 2a. XML - a declaration or a root element.
        if (first == '<')
        {
            var confidence = text.TrimStart().StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ? "high" : "medium";
            evidence.Add(confidence == "high" ? "starts with an XML declaration '<?xml'" : "starts with an element tag '<'");
            return new SourceFormatDetection("xml", confidence, evidence);
        }

        // 2b. JSON - an object or array; a second top-level object separated only by whitespace means NDJSON.
        if (first == '{' || first == '[')
        {
            if (first == '{' && NdjsonBreak.IsMatch(text))
            {
                evidence.Add("newline-delimited JSON objects (one record per line)");
                return new SourceFormatDetection("ndjson", "high", evidence);
            }

            evidence.Add(first == '[' ? "starts with a JSON array '['" : "starts with a JSON object '{'");
            return new SourceFormatDetection("json", "high", evidence);
        }

        // 2c. Delimited text - profile the candidate delimiters for consistency across the first lines.
        var (delimiter, option, consistency, count) = ProfileDelimited(text);
        if (count > 0)
        {
            var confidence = consistency >= 0.9 ? "high" : "medium";
            var name = delimiter == '\t' ? "tab" : $"'{delimiter}'";
            evidence.Add($"delimited text: {count} {name}-separated field(s), {(int)(consistency * 100)}% consistent across sampled lines");
            return new SourceFormatDetection("csv", confidence, evidence, option);
        }

        // A single-column text file, or an unrecognizable shape: prefer the extension when it named something.
        if (extType is not null)
        {
            evidence.Add($"no clear structure; fell back to extension ({extType})");
            return new SourceFormatDetection(extType, "low", evidence);
        }

        evidence.Add("no delimiter or structure detected; treating as single-column text");
        return new SourceFormatDetection("csv", "low", evidence);
    }

    /// <summary>Decodes the head for shape detection, honoring a UTF-8/UTF-16 BOM and otherwise reading Latin-1 (a
    /// total, lossless byte-to-char mapping) so only the structural ASCII characters matter and no decode can throw.</summary>
    private static (string Text, string? EncodingNote) DecodeHead(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
        {
            return (Encoding.UTF8.GetString(head[3..]), "UTF-8 BOM");
        }

        if (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE)
        {
            return (Encoding.Unicode.GetString(head[2..]), "UTF-16 LE BOM");
        }

        if (head.Length >= 2 && head[0] == 0xFE && head[1] == 0xFF)
        {
            return (Encoding.BigEndianUnicode.GetString(head[2..]), "UTF-16 BE BOM");
        }

        return (Encoding.Latin1.GetString(head), null);
    }

    /// <summary>
    /// Profiles delimited text: for each candidate delimiter, how many fields the first data line has and how
    /// consistently the sampled lines repeat that count. Returns the best (most consistent, then most fields)
    /// candidate; a count of 0 means no delimiter looked like a column separator.
    /// </summary>
    private static (char Delimiter, string? Option, double Consistency, int Count) ProfileDelimited(string text)
    {
        var lines = text
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .Take(20)
            .ToList();

        if (lines.Count == 0)
        {
            return (',', null, 0, 0);
        }

        (char Delimiter, string? Option, double Consistency, int Count) best = (',', null, 0, 0);
        foreach (var (ch, option) in CsvDelimiters)
        {
            var firstCount = lines[0].Count(c => c == ch);
            if (firstCount == 0)
            {
                continue;
            }

            var agree = lines.Count(l => l.Count(c => c == ch) == firstCount);
            var consistency = (double)agree / lines.Count;

            // Fields = delimiters + 1; prefer higher consistency, then more fields.
            if (consistency > best.Consistency || (consistency == best.Consistency && firstCount + 1 > best.Count))
            {
                best = (ch, option, consistency, firstCount + 1);
            }
        }

        return best;
    }
}
