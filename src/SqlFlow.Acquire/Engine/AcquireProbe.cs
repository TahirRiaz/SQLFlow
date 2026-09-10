namespace SqlFlow.Acquire.Engine;

/// <summary>
/// A per-page capture sink for the debugger: it receives, for every request the engine issues, the resolved method
/// and URL, the (redacted) request headers, the response status, headers, timing, size, record count, and a bounded
/// body preview. The normal run path passes no probe; the control-plane test invoke passes one to render the
/// request/response inspector without landing anything to the lake.
/// </summary>
public interface IAcquireProbe
{
    void Page(AcquirePageProbe probe);
}

/// <summary>One captured request/response for the debugger.</summary>
public sealed record AcquirePageProbe
{
    public required int Iteration { get; init; }
    public required int Page { get; init; }
    public required string Method { get; init; }
    public required string Url { get; init; }
    public required IReadOnlyDictionary<string, string> RequestHeaders { get; init; }
    public required int Status { get; init; }
    public required IReadOnlyDictionary<string, string> ResponseHeaders { get; init; }
    public required string? ContentType { get; init; }
    public required long Bytes { get; init; }
    public required int RecordCount { get; init; }
    public required double DurationMs { get; init; }
    public required string BodyPreview { get; init; }
    public string? LandedTo { get; init; }
}

/// <summary>Collects probes into a list; the control plane serializes them into the debug response.</summary>
public sealed class CollectingProbe : IAcquireProbe
{
    private readonly List<AcquirePageProbe> _pages = [];

    public IReadOnlyList<AcquirePageProbe> Pages => _pages;

    public void Page(AcquirePageProbe probe) => _pages.Add(probe);
}

/// <summary>Shared probe helpers, so every transport renders the debugger body preview the same way.</summary>
internal static class TransportProbe
{
    private const int MaxPreviewBytes = 8192;

    /// <summary>A bounded UTF-8 view of a payload for the debugger's response-body panel. Binary payloads render as
    /// their best-effort UTF-8 decoding; the size and content-type in the probe tell the reader when that is not text.</summary>
    internal static string Preview(ReadOnlySpan<byte> body)
    {
        var take = Math.Min(body.Length, MaxPreviewBytes);
        var text = System.Text.Encoding.UTF8.GetString(body[..take]);
        return body.Length > MaxPreviewBytes ? text + "\n… (truncated)" : text;
    }
}
