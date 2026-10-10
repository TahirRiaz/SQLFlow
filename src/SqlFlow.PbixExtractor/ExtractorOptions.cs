using System.Text;
using SqlFlow.Lineage.Collection;

namespace SqlFlow.PbixExtractor;

/// <summary>
/// The extractor's configuration (section <c>PbixExtractor</c>). Environment:
/// <c>PbixExtractor__ApiKey</c>, <c>PbixExtractor__MaxUploadMegabytes</c>, <c>PbixExtractor__MaxConcurrent</c>,
/// <c>PbixExtractor__QueueLimit</c>. The binary itself is located the way every other caller locates it
/// (<c>SQLFLOW_PBIX_EXTRACT</c>, then beside the app, then <c>PATH</c>).
/// </summary>
public sealed class ExtractorOptions
{
    public const string SectionName = "PbixExtractor";

    /// <summary>The shared key a caller presents in <see cref="ReportExtractionProtocol.KeyHeader"/>. The service is meant
    /// to be reachable only from the control plane's private network; the key is the second fence, so a workload
    /// that lands on that network still cannot use it.</summary>
    public string? ApiKey { get; set; }

    /// <summary>The largest report accepted. Power BI's own service caps a report at 1 GB; most are far smaller.</summary>
    public int MaxUploadMegabytes { get; set; } = 256;

    /// <summary>How many reports are extracted at once. Decompressing a model is memory-hungry, so this stays small.</summary>
    public int MaxConcurrent { get; set; } = 2;

    /// <summary>How many further requests may wait for a free slot before new ones are refused as busy.</summary>
    public int QueueLimit { get; set; } = 8;

    public long MaxUploadBytes => MaxUploadMegabytes * 1024L * 1024L;

    /// <summary>Throws a startup error naming the first invalid setting.</summary>
    public void Validate()
    {
        if (string.IsNullOrEmpty(ApiKey) || Encoding.UTF8.GetByteCount(ApiKey) < 32)
        {
            throw new InvalidOperationException(
                "PbixExtractor:ApiKey is required and must be at least 32 bytes. Set it from a secret, and give the "
                + "control plane the same value (ControlPlane:PowerAI:ReportExtraction:ApiKey).");
        }

        if (MaxUploadMegabytes is < 1 or > 1024)
        {
            throw new InvalidOperationException("PbixExtractor:MaxUploadMegabytes must be between 1 and 1024.");
        }

        if (MaxConcurrent is < 1 or > 16)
        {
            throw new InvalidOperationException("PbixExtractor:MaxConcurrent must be between 1 and 16.");
        }

        if (QueueLimit is < 0 or > 256)
        {
            throw new InvalidOperationException("PbixExtractor:QueueLimit must be between 0 and 256.");
        }
    }
}
