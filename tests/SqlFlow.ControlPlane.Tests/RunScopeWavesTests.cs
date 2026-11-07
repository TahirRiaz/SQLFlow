using SqlFlow.Catalog;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// How the flows one execution runs are put in waves (<see cref="RunScopeExpander.InWaves"/>): by the dependencies among
/// them alone, with a cycle and what waits behind it in one last wave, as lineage places a cycle.
/// </summary>
public sealed class RunScopeWavesTests
{
    [Fact]
    public void MembersWithoutEdgesBetweenThem_RunInOneWave_WhateverTheRepositoryWave()
    {
        var a = Member("a", repositoryWave: 1);
        var b = Member("b", repositoryWave: 4);
        var c = Member("c", repositoryWave: 6);

        var waves = RunScopeExpander.InWaves([c, a, b], []);

        Assert.Equal(new[] { ("a", 0), ("b", 0), ("c", 0) }, Shape(waves));
    }

    [Fact]
    public void AChain_TakesAWavePerLink_AndAMemberWaitsForItsLongestChain()
    {
        var load = Member("load");
        var key = Member("key");
        var deliver = Member("deliver");
        var check = Member("check");

        // check waits for load directly and for deliver, two links behind load: it goes after deliver.
        var waves = RunScopeExpander.InWaves(
            [check, deliver, key, load],
            [Edge(load, key), Edge(key, deliver), Edge(deliver, check), Edge(load, check)]);

        Assert.Equal(new[] { ("load", 0), ("key", 1), ("deliver", 2), ("check", 3) }, Shape(waves));
    }

    [Fact]
    public void AnEdgeWithAnEndOutsideTheSet_OrdersNothing_EvenAsAPathThroughIt()
    {
        var first = Member("first");
        var second = Member("second");
        var notRun = Guid.NewGuid();

        var waves = RunScopeExpander.InWaves(
            [first, second],
            [Edge(first.PipelineId!.Value, notRun), Edge(notRun, second.PipelineId!.Value), Edge(Guid.NewGuid(), first.PipelineId!.Value)]);

        Assert.Equal(new[] { ("first", 0), ("second", 0) }, Shape(waves));
    }

    [Fact]
    public void ARepeatedEdge_CountsOnce()
    {
        var a = Member("a");
        var b = Member("b");

        var waves = RunScopeExpander.InWaves([a, b], [Edge(a, b), Edge(a, b)]);

        Assert.Equal(new[] { ("a", 0), ("b", 1) }, Shape(waves));
    }

    [Fact]
    public void ACycle_AndWhatWaitsBehindIt_GoToOneLastWave()
    {
        var start = Member("start");
        var left = Member("left");
        var right = Member("right");
        var behind = Member("behind");
        var free = Member("free");

        var waves = RunScopeExpander.InWaves(
            [start, left, right, behind, free],
            [Edge(start, left), Edge(left, right), Edge(right, left), Edge(right, behind)]);

        Assert.Equal(new[] { ("free", 0), ("start", 0), ("behind", 1), ("left", 1), ("right", 1) }, Shape(waves));
    }

    [Fact]
    public void ASetThatIsOneCycle_StartsAtWaveZero()
    {
        var a = Member("a");
        var b = Member("b");

        var waves = RunScopeExpander.InWaves([a, b], [Edge(a, b), Edge(b, a)]);

        Assert.Equal(new[] { ("a", 0), ("b", 0) }, Shape(waves));
    }

    [Fact]
    public void AMemberWithoutAPipelineId_HasNoEdges()
    {
        var named = new RunScopeMember("named", "ing", 3, CatalogPipeline.DefaultBatch);
        var other = Member("other");

        var waves = RunScopeExpander.InWaves([named, other], [Edge(other.PipelineId!.Value, Guid.NewGuid())]);

        Assert.Equal(new[] { ("named", 0), ("other", 0) }, Shape(waves));
    }

    [Fact]
    public void NoMembers_NoWaves()
        => Assert.Empty(RunScopeExpander.InWaves([], [Edge(Guid.NewGuid(), Guid.NewGuid())]));

    private static RunScopeMember Member(string name, int repositoryWave = 0)
        => new(name, "ing", repositoryWave, CatalogPipeline.DefaultBatch, Guid.NewGuid());

    private static (Guid From, Guid To) Edge(RunScopeMember from, RunScopeMember to)
        => (from.PipelineId!.Value, to.PipelineId!.Value);

    private static (Guid From, Guid To) Edge(Guid from, Guid to) => (from, to);

    private static (string, int)[] Shape(IReadOnlyList<RunScopeMember> members)
        => members.Select(m => (m.FlowName, m.Wave)).ToArray();
}
