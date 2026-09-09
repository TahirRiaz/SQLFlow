using SqlFlow.Core.Model;

namespace SqlFlow.Core.Abstractions;

/// <summary>
/// Receives <see cref="FlowEvent"/>s from the engine as they happen. The host implements it to stream
/// progress to the client - print to a CLI, push over SignalR/websocket to a GUI, etc. Called
/// synchronously and in order; implementations must be cheap and not throw.
/// </summary>
public interface IFlowEventSink
{
    void Publish(FlowEvent flowEvent);
}
