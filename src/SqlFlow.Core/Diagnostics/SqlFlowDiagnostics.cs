using System.Diagnostics;

namespace SqlFlow.Core.Diagnostics;

/// <summary>
/// Tracing entry point. Stages emit <see cref="Activity"/> spans on this source so timings and
/// failures can be observed live or exported via OpenTelemetry - the basis for pinning down
/// performance bottlenecks and tracing bugs across a run.
/// </summary>
public static class SqlFlowDiagnostics
{
    public const string ActivitySourceName = "SqlFlow";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
}
