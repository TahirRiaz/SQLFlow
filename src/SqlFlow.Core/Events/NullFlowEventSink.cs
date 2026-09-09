using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;

namespace SqlFlow.Core.Events;

/// <summary>The default event sink: discards everything. Used when no client is listening.</summary>
public sealed class NullFlowEventSink : IFlowEventSink
{
    public static readonly NullFlowEventSink Instance = new();

    public void Publish(FlowEvent flowEvent)
    {
    }
}
