using System.Collections.Concurrent;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;
using SqlFlow.Orchestration;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The run's origin (the commit, snapshot or synced copy it executes, and the folder it reads) is the first event of
/// every collector an executor builds, and reaches the live sink as well as the artifact, so the live feed and
/// run.json count the same events in the same order.
/// </summary>
public sealed class RunOriginTests
{
    [Fact]
    public void ACollector_PublishesTheOriginFirst_ToTheArtifactAndTheLiveSink()
    {
        var live = new RecordingSink();
        var options = new DocumentExecutionOptions
        {
            EventSink = live,
            Origin = "executing commit 17662b51513bb2a7102c2587f8dbd9201fef2b94 of repository 'welldb', checked out at C:\\cache\\17662b5",
        };

        var events = options.CreateEventCollector();
        events.Publish(new FlowEvent { Message = "Flow 'f' started -> [pre].[T]" });

        Assert.Collection(
            events.Records,
            origin =>
            {
                Assert.Equal(options.Origin, origin.Message);
                Assert.Equal("run.origin", origin.Step);
            },
            started => Assert.Equal("Flow 'f' started -> [pre].[T]", started.Message));
        Assert.Equal([options.Origin, "Flow 'f' started -> [pre].[T]"], live.Messages);
    }

    [Fact]
    public void ACollector_WithoutAnOrigin_StartsEmpty()
    {
        var events = new DocumentExecutionOptions().CreateEventCollector();

        Assert.Empty(events.Records);
    }

    private sealed class RecordingSink : IFlowEventSink
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyList<string> Messages => [.. _messages];

        public void Publish(FlowEvent flowEvent) => _messages.Enqueue(flowEvent.Message);
    }
}
