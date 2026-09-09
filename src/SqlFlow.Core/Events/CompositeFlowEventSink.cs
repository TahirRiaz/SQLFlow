using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;

namespace SqlFlow.Core.Events;

/// <summary>Fans one event out to several sinks, in registration order. Used to attach a per-run sink (the
/// node's live catalog writer, the executor's artifact collector) alongside a host-wide sink (the CLI console)
/// without either knowing about the other. Honors the sink contract transitively: it only calls sinks that
/// promise not to throw, so it adds no guarding of its own.</summary>
public sealed class CompositeFlowEventSink : IFlowEventSink
{
    private readonly IFlowEventSink[] _sinks;

    public CompositeFlowEventSink(params IFlowEventSink[] sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        _sinks = sinks;
    }

    public void Publish(FlowEvent flowEvent)
    {
        foreach (var sink in _sinks)
        {
            sink.Publish(flowEvent);
        }
    }
}
