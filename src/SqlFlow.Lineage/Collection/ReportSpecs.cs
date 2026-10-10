namespace SqlFlow.Lineage.Collection;

/// <summary>What one report specification holds, counted, for a person deciding whether it is the report they meant.</summary>
/// <param name="Pages">The report pages.</param>
/// <param name="Visuals">The visuals across every page that ask a question.</param>
/// <param name="Tables">The semantic model's tables.</param>
/// <param name="Measures">The model's measures.</param>
/// <param name="Relationships">The model's relationships.</param>
/// <param name="ResolvedTables">The model tables whose Power Query source names a warehouse table.</param>
/// <param name="Warnings">What the extractor declined to extract, and why.</param>
public sealed record ReportSpecSummary(
    int Pages, int Visuals, int Tables, int Measures, int Relationships, int ResolvedTables,
    IReadOnlyList<string> Warnings);

/// <summary>
/// The one place a Power BI report specification is named, located, produced, checked, and made storable. A
/// specification is the YAML <c>tools/pbix-extract</c> writes for one <c>.pbix</c>: the report's pages, visuals and
/// semantic model as a node/edge graph. It is the unit that crosses every boundary in this feature: the collector
/// reads it from a committed file or the semantic layer, the control plane stores it, and the CLI writes it, so all
/// of them go through here rather than each deciding what a specification is.
/// </summary>
public static class ReportSpecs
{
    /// <summary>The suffix a committed specification carries: the report's own file name plus <c>.yaml</c>
    /// (<c>Sales.pbix.yaml</c>), so the report it describes is readable from the name and the report label it
    /// produces matches the one extracting the <c>.pbix</c> directly would.</summary>
    public const string FileSuffix = ".pbix.yaml";

    /// <summary>The largest specification accepted from any source. A real report's specification is well under a
    /// megabyte; the bound exists so a hostile or corrupt one cannot exhaust memory.</summary>
    public const int MaxBytes = 32 * 1024 * 1024;

    /// <summary>The longest report label, matching the catalog column every report row keys on.</summary>
    public const int MaxReportFileLength = 260;

    /// <summary>The environment variable naming the extractor explicitly.</summary>
    public static string ExtractorPathVariable => PbixExtractTool.PathVariable;

    /// <summary>Whether <paramref name="path"/> names a committed specification.</summary>
    public static bool IsSpecFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.EndsWith(FileSuffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The report label a specification file stands for: its name without the trailing <c>.yaml</c>
    /// (<c>team/Sales.pbix.yaml</c> gives <c>team/Sales.pbix</c>), or, for any other YAML file, its name without the
    /// extension.</summary>
    public static string LabelOf(string specPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(specPath);
        if (specPath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
        {
            return specPath[..^".yaml".Length];
        }

        return specPath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
            ? specPath[..^".yml".Length]
            : specPath;
    }

    /// <summary>
    /// Why <paramref name="reportFile"/> cannot label a report, or null when it can. A label is part of the key every
    /// report row is stored under, joined with <c>#</c>, so it may not contain one; it is a relative, forward-slashed
    /// path so the same report reads the same on every machine.
    /// </summary>
    public static string? ReportFileProblem(string? reportFile)
    {
        if (string.IsNullOrWhiteSpace(reportFile))
        {
            return "A report file name is required.";
        }

        if (reportFile.Length > MaxReportFileLength)
        {
            return $"The report file name is longer than {MaxReportFileLength} characters.";
        }

        if (!string.Equals(reportFile, reportFile.Trim(), StringComparison.Ordinal))
        {
            return "The report file name may not start or end with whitespace.";
        }

        if (reportFile.Any(c => char.IsControl(c) || c is '#' or '\\'))
        {
            return "The report file name may not contain '#', a backslash, or a control character.";
        }

        if (reportFile.StartsWith('/') || reportFile.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            return "The report file name must be a relative path with no empty, '.' or '..' segments.";
        }

        return null;
    }

    /// <summary>The extractor's location on this machine, or null when it has none (see
    /// <see cref="ExtractorPathVariable"/>).</summary>
    public static string? LocateExtractor() => PbixExtractTool.Locate();

    /// <summary>Runs the extractor over one report and returns its specification in canonical form.</summary>
    /// <param name="executable">The extractor, from <see cref="LocateExtractor"/>.</param>
    /// <param name="pbixPath">The report.</param>
    /// <param name="reportFile">The label the report's pages carry.</param>
    /// <exception cref="PbixExtractException">The report could not be extracted.</exception>
    public static string Extract(string executable, string pbixPath, string reportFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(pbixPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportFile);
        return Normalize(PbixExtractTool.RunToYaml(executable, pbixPath, reportFile), $"report '{reportFile}'");
    }

    /// <summary>Checks a specification and rewrites it into the canonical, credential-redacted form the semantic
    /// layer stores.</summary>
    /// <param name="yaml">The specification.</param>
    /// <param name="source">Names the specification in any error.</param>
    /// <exception cref="PbixExtractException">The text is not a readable specification.</exception>
    public static string Normalize(string yaml, string source)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return PbixExtractTool.Normalize(yaml, source);
    }

    /// <summary>Reads a specification and counts what it holds.</summary>
    /// <exception cref="PbixExtractException">The text is not a readable specification.</exception>
    public static ReportSpecSummary Inspect(string yaml, string source)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var parsed = PbixExtractTool.Parse(yaml, source);
        return new ReportSpecSummary(
            parsed.Pages.Count,
            parsed.Pages.Sum(p => p.Visuals?.Count ?? 0),
            parsed.Model.Tables.Count(t => t.Name.Length > 0),
            parsed.Model.Tables.Sum(t => t.Fields.Count(f => f.Kind == "measure")),
            parsed.Model.Relationships.Count,
            parsed.ModelSources.Count,
            parsed.Warnings);
    }

    /// <summary>The lowercase-hex SHA-256 of a specification's text, the identity a stored copy is compared by.</summary>
    public static string Hash(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(yaml))).ToLowerInvariant();
    }
}

/// <summary>
/// The wire contract between the control plane and the isolated extractor service (<c>SqlFlow.PbixExtractor</c>),
/// named once for both sides.
/// </summary>
public static class ReportExtractionProtocol
{
    /// <summary>The header carrying the shared key.</summary>
    public const string KeyHeader = "X-SqlFlow-Extractor-Key";

    /// <summary>The extraction route: <c>POST</c> the raw <c>.pbix</c> bytes as <c>application/octet-stream</c>, with
    /// the report label as <c>?reportFile=</c>. The answer is the canonical specification as
    /// <see cref="SpecMediaType"/>, or a problem document whose <c>detail</c> says why the report was refused.</summary>
    public const string ExtractPath = "/v1/extract";

    /// <summary>The media type of a successful answer.</summary>
    public const string SpecMediaType = "application/yaml";
}

/// <summary>The origins a specification stored in the semantic layer can have.</summary>
public static class ReportSpecOrigin
{
    /// <summary>A person uploaded it (a <c>.pbix</c> extracted in the isolated extractor, or a specification the CLI
    /// published). It stays until a person deletes it.</summary>
    public const string Upload = "upload";

    /// <summary>A sync ran the extractor over a <c>.pbix</c> the repository declares and kept what it produced, so a
    /// later sync on a machine without the extractor or the file serves the same report instead of dropping it.
    /// Replaced by the next sync that can extract the report, and removed once the repository stops declaring it.</summary>
    public const string Extracted = "extracted";

    /// <summary>Whether <paramref name="value"/> is one of the origins above.</summary>
    public static bool IsKnown(string? value)
        => string.Equals(value, Upload, StringComparison.Ordinal)
            || string.Equals(value, Extracted, StringComparison.Ordinal);
}

/// <summary>A specification held in the semantic layer, as a sync hands it to the collector.</summary>
/// <param name="SubscriberKey">The node key of the subscriber it belongs to.</param>
/// <param name="ReportFile">The report label its pages carry.</param>
/// <param name="Origin">A <see cref="ReportSpecOrigin"/> value.</param>
/// <param name="Yaml">The canonical specification.</param>
public sealed record StoredReportSpec(string SubscriberKey, string ReportFile, string Origin, string Yaml);

/// <summary>A specification a sync produced by running the extractor, to be kept in the semantic layer.</summary>
/// <param name="ReportFile">The report label its pages carry.</param>
/// <param name="Yaml">The canonical specification.</param>
public sealed record ExtractedReportSpec(string ReportFile, string Yaml);
