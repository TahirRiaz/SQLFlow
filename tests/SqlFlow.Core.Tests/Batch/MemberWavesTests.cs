using SqlFlow.Lineage.Graph;
using Xunit;

namespace SqlFlow.Tests.Batch;

/// <summary>
/// The waves of a set of flows by the dependencies among them alone (<see cref="MemberWaves"/>), as a batch flow keys its
/// members, by name: the comparer decides which names are one member, a name listed twice sits where it was first
/// listed, and a cycle and what waits behind it go to one fallback wave reported as unordered.
/// </summary>
public sealed class MemberWavesTests
{
    [Fact]
    public void NamesCompareByTheGivenComparer()
    {
        var placed = MemberWaves.Compute(
            ["Load", "deliver"], [("load", "DELIVER")], StringComparer.OrdinalIgnoreCase);

        Assert.Equal(new[] { new MemberWave(0, false), new MemberWave(1, false) }, placed);
    }

    [Fact]
    public void ANameListedTwice_SitsWhereItsFirstListingDoes()
    {
        var placed = MemberWaves.Compute(["a", "b", "a"], [("a", "b")], StringComparer.Ordinal);

        Assert.Equal(new[] { new MemberWave(0, false), new MemberWave(1, false), new MemberWave(0, false) }, placed);
    }

    [Fact]
    public void ACycle_IsReportedUnordered_InTheWaveAfterTheOrderedOnes()
    {
        var placed = MemberWaves.Compute(
            ["root", "ring1", "ring2", "after"],
            [("root", "ring1"), ("ring1", "ring2"), ("ring2", "ring1"), ("root", "after")],
            StringComparer.Ordinal);

        Assert.Equal(
            new[] { new MemberWave(0, false), new MemberWave(2, true), new MemberWave(2, true), new MemberWave(1, false) },
            placed);
    }

    [Fact]
    public void AnEdgeOutsideTheSet_AndASelfEdge_OrderNothing()
    {
        var placed = MemberWaves.Compute(
            ["a", "b"], [("a", "elsewhere"), ("elsewhere", "b"), ("b", "b")], StringComparer.Ordinal);

        Assert.Equal(new[] { new MemberWave(0, false), new MemberWave(0, false) }, placed);
    }

    [Fact]
    public void APathThroughPassedOverFlows_OrdersTheMembersAtItsEnds_AndOnlyThroughThem()
    {
        // load feeds a passed-over transform, which feeds a passed-over merge, which feeds check: check still waits for
        // load. other is reached only through a flow that is not passed over, and orders nothing.
        var placed = MemberWaves.Compute(
            ["check", "load", "other"],
            [("load", "transform"), ("transform", "merge"), ("merge", "check"), ("load", "elsewhere"), ("elsewhere", "other")],
            StringComparer.Ordinal,
            passedOver: ["transform", "merge"]);

        Assert.Equal(new[] { new MemberWave(1, false), new MemberWave(0, false), new MemberWave(0, false) }, placed);
    }

    [Fact]
    public void APassedOverKeyThatIsAMember_CountsAsAMember_AndAPassedOverCycleOrdersNothingItself()
    {
        // b is a member and passed over at once: it stays a member, in its own wave. The passed-over pair p and q feed
        // each other, which neither loops forever nor makes a cycle of the members on either side of them.
        var placed = MemberWaves.Compute(
            ["a", "b", "c"],
            [("a", "b"), ("b", "p"), ("p", "q"), ("q", "p"), ("q", "c")],
            StringComparer.Ordinal,
            passedOver: ["b", "p", "q"]);

        Assert.Equal(new[] { new MemberWave(0, false), new MemberWave(1, false), new MemberWave(2, false) }, placed);
    }
}
