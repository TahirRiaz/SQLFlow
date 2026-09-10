namespace SqlFlow.Sources;

/// <summary>
/// Receives the coarse progress phases a <see cref="SourceDiscoveryService"/> emits as it runs (resolving the store,
/// detecting the format, sniffing a delimiter, reading the schema, sampling records). A caller that wants to show the
/// operation live implements this to forward each phase to its own sink; the control plane forwards them into the
/// activity trace the GUI's bottom panel tails, so an operator sees each phase and exactly where a failure lands
/// rather than only a terminal result. Discovery runs headless when no progress sink is supplied (a null sink).
/// </summary>
public interface IDiscoveryProgress
{
    /// <summary>Reports one progress phase. <paramref name="phase"/> is a short phase tag (e.g. "format",
    /// "delimiter", "schema"); <paramref name="message"/> is the human-readable line.</summary>
    Task ReportAsync(string phase, string message, CancellationToken ct = default);
}
