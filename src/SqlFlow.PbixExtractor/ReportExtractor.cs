using SqlFlow.Lineage.Collection;

namespace SqlFlow.PbixExtractor;

/// <summary>Turns one report file on disk into its canonical specification.</summary>
public interface IReportExtractor
{
    /// <summary>Whether the extractor binary is present, so readiness can say the service cannot do its job.</summary>
    bool IsAvailable { get; }

    /// <summary>Extracts <paramref name="pbixPath"/>, labelling its pages <paramref name="reportFile"/>.</summary>
    /// <exception cref="PbixExtractException">The report could not be extracted.</exception>
    Task<string> ExtractAsync(string pbixPath, string reportFile, CancellationToken ct);
}

/// <summary>The production extractor: <c>tools/pbix-extract</c>, run through the same <see cref="ReportSpecs"/> path a
/// sync on a developer machine uses.</summary>
public sealed class PbixReportExtractor : IReportExtractor
{
    private readonly string? _executable = ReportSpecs.LocateExtractor();

    public bool IsAvailable => _executable is not null;

    public Task<string> ExtractAsync(string pbixPath, string reportFile, CancellationToken ct)
    {
        if (_executable is null)
        {
            throw new PbixExtractException(
                $"the 'pbix-extract' tool was not found; set {ReportSpecs.ExtractorPathVariable} to its location");
        }

        // The tool runs as a child process with its own bounded lifetime; the request thread is released while it
        // works rather than blocked on the wait.
        var executable = _executable;
        return Task.Run(() =>
        {
            try
            {
                return ReportSpecs.Extract(executable, pbixPath, reportFile);
            }
            catch (PbixExtractException ex)
            {
                // Where the tool sits inside this container means nothing to the person who uploaded the report.
                throw new PbixExtractException(
                    ex.Message.Replace($"'{executable}'", "pbix-extract", StringComparison.Ordinal), ex);
            }
        }, ct);
    }
}
