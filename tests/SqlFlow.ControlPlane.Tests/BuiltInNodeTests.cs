using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Configuration;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Which pools the fleet page says this control plane's own node serves (<see cref="NodeEndpoints.BuiltInNodeOf"/>):
/// the default pool and the pools <c>ControlPlane:Worker:Pools</c> names, as the node polls for them, and none at all
/// on an API-only replica, so a deployment whose workers an autoscaler starts never shows a built-in node.
/// </summary>
public sealed class BuiltInNodeTests
{
    [Fact]
    public void TheDefaultPool_IsAlwaysServed_WithTheNodesRunSlots()
    {
        var worker = new WorkerOptions { MaxConcurrentRuns = 6 };

        Assert.Equal(new BuiltInNodeDto("cp-node", 6), NodeEndpoints.BuiltInNodeOf(string.Empty, worker, "cp-node"));
        Assert.Null(NodeEndpoints.BuiltInNodeOf("etl", worker, "cp-node"));
    }

    [Fact]
    public void ANamedPool_IsServedWhenTheNodeListsIt_ByItsNormalizedKey()
    {
        var worker = new WorkerOptions { Pools = [" etl ", "gpu"] };

        Assert.Equal(new BuiltInNodeDto("cp-node", 4), NodeEndpoints.BuiltInNodeOf("etl", worker, "cp-node"));
        Assert.Equal(new BuiltInNodeDto("cp-node", 4), NodeEndpoints.BuiltInNodeOf("gpu", worker, "cp-node"));
        Assert.Equal(new BuiltInNodeDto("cp-node", 4), NodeEndpoints.BuiltInNodeOf(string.Empty, worker, "cp-node"));
        Assert.Null(NodeEndpoints.BuiltInNodeOf("Etl", worker, "cp-node"));
    }

    [Fact]
    public void AnApiOnlyReplica_ServesNoPool()
    {
        var worker = new WorkerOptions { Enabled = false, Pools = ["etl"] };

        Assert.Null(NodeEndpoints.BuiltInNodeOf(string.Empty, worker, "cp-node"));
        Assert.Null(NodeEndpoints.BuiltInNodeOf("etl", worker, "cp-node"));
    }

    [Fact]
    public void ANodeWithoutAName_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => NodeEndpoints.BuiltInNodeOf(string.Empty, new WorkerOptions(), " "));
    }
}
