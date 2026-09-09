using SqlFlow.Acquire.Runtime;
using SqlFlow.Core.Acquire;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Acquire.Engine;

/// <summary>
/// A transport fetches raw payloads for one resolved iteration and hands each to the landing pipeline. It owns its
/// own fetch loop (HTTP pagination, or listing and downloading objects/entities); the engine owns the fan-out that
/// produces the per-iteration <see cref="AcquireFetch"/> contexts and the shared reliability primitives.
/// </summary>
public interface IAcquireTransport
{
    bool CanHandle(AcquireTransport transport);

    Task FetchAsync(AcquireFetch fetch, CancellationToken ct);
}

/// <summary>Everything a transport needs to fetch and land one iteration: the source, the bound variables, the
/// applied auth, the sink, the watermark, the log, and (for HTTP) the executor/client and secret resolver.</summary>
public sealed class AcquireFetch
{
    public required AcquireSource Source { get; init; }
    public required TemplateContext Vars { get; init; }
    public required AppliedAuth Auth { get; init; }
    public required LandingPipeline Landing { get; init; }
    public required WatermarkState Watermark { get; init; }
    public required IRunEventSink Log { get; init; }
    public required ISecretResolver Secrets { get; init; }

    /// <summary>The HTTP executor for the data host; null for a non-HTTP transport.</summary>
    public HttpExecutor? Http { get; init; }

    /// <summary>The 0-based iteration index, for the debugger's page grouping.</summary>
    public int Iteration { get; init; }

    /// <summary>The debugger capture sink, or null on a normal run.</summary>
    public IAcquireProbe? Probe { get; init; }

    /// <summary>An upper bound on pages for this fetch (the Test invoke caps it); null uses the flow's own max.</summary>
    public int? MaxPagesOverride { get; init; }

    /// <summary>Pages/objects fetched in this iteration; the transport increments it, the engine sums across iterations.</summary>
    public int Pages { get; set; }
}
