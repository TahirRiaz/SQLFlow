using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;

namespace SqlFlow.Execution;

/// <summary>Streams engine events to the console as they happen (skips low-level Trace events). The engine wiring
/// registers it as the default <see cref="IFlowEventSink"/>; a host with no console (the control plane, a worker)
/// overrides the registration with its own sink, and a host whose stdout carries machine output (the CLI's
/// --json mode) re-registers it aimed at standard error so the data stream stays pure.</summary>
public sealed class ConsoleFlowEventSink : IFlowEventSink
{
    private readonly TextWriter? _writer;

    /// <summary>Streams to standard output (the default registration).</summary>
    public ConsoleFlowEventSink()
    {
    }

    /// <summary>Streams to <paramref name="writer"/> (e.g. <see cref="Console.Error"/> when stdout is JSON).</summary>
    public ConsoleFlowEventSink(TextWriter writer) => _writer = writer;

    public void Publish(FlowEvent flowEvent)
    {
        if (flowEvent.Level is FlowEventLevel.Trace or FlowEventLevel.Debug)
        {
            return;
        }

        var marker = flowEvent.Level switch
        {
            FlowEventLevel.Error => "x ",
            FlowEventLevel.Warning => "! ",
            _ => ". ",
        };

        // Prefix with flow name + short run id so concurrent runs stay attributable in one stream.
        var tag = flowEvent.FlowName is { } name
            ? $"[{name}#{flowEvent.RunId.ToString("N")[..8]}] "
            : string.Empty;

        (_writer ?? Console.Out).WriteLine($"  {tag}{marker}{flowEvent.Message}");
    }
}
