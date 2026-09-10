using SqlFlow.Core;
using SqlFlow.Core.Ingestion;
using SqlFlow.SqlServer.Ingestion;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// The work-table lease: the engine's guarantee that two executions of ONE flow never share its canonical staging
/// table. The regression this closes was observed in production: two overlapping executions of one ing flow each
/// ran DROP-then-CREATE over [raw].[arc_&lt;table&gt;_&lt;flowId&gt;], one staged into the other's table, and the
/// second failed with "Invalid object name" when the first dropped it on the way out. The DB-backed cases need a
/// reachable sink (they take a real application lock on it) and skip without one; the resource-naming cases are
/// pure and always run.
/// </summary>
[Trait("Category", "Integration")]
public sealed class WorkTableLeaseIntegrationTests
{
    private static RelationalObject Staging(string name)
        => new() { Database = "db", Schema = "raw", Name = name };

    private static string UniqueName(string suffix)
        => $"arc_lease_{Guid.NewGuid():N}_{suffix}";

    [SkippableFact]
    public async Task SecondExecutionOfTheSameFlow_IsRefused_WhileTheFirstHoldsTheLease()
    {
        var cs = IntegrationDb.Require();
        var staging = Staging(UniqueName("held"));

        await using var first = await WorkTableLease.AcquireAsync(cs, staging, "flow_under_test", 0, CancellationToken.None);

        // The second execution asks for the same flow's tables and is refused rather than dropping them. Zero wait
        // keeps the test quick; the run's real budget is WorkTableLease.LockWaitMs.
        var refused = await Assert.ThrowsAsync<ConcurrentFlowExecutionException>(
            () => WorkTableLease.AcquireAsync(cs, staging, "flow_under_test", 0, CancellationToken.None));

        Assert.Equal("flow_under_test", refused.Flow);
        Assert.Equal($"[raw].[{staging.Name}]", refused.WorkTable);
        Assert.True(refused.ReturnCode < 0);
        // The message has to tell an operator what happened without reading the code.
        Assert.Contains("already being executed", refused.Message, StringComparison.Ordinal);
        Assert.Contains(staging.Name, refused.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task TwoDifferentFlows_NeverContend()
    {
        var cs = IntegrationDb.Require();

        // Distinct flows stage through distinct tables, so their leases are distinct resources: a busy flow must
        // never block an unrelated one.
        await using var a = await WorkTableLease.AcquireAsync(cs, Staging(UniqueName("a")), "flow_a", 0, CancellationToken.None);
        await using var b = await WorkTableLease.AcquireAsync(cs, Staging(UniqueName("b")), "flow_b", 0, CancellationToken.None);

        Assert.NotEqual(a.Resource, b.Resource);
    }

    [SkippableFact]
    public async Task DisposingTheLease_HandsTheFlowToTheNextExecution()
    {
        var cs = IntegrationDb.Require();
        var staging = Staging(UniqueName("handover"));

        var first = await WorkTableLease.AcquireAsync(cs, staging, "flow_under_test", 0, CancellationToken.None);
        // Free on arrival: the server granted it without waiting (elapsed time would only measure the round trip).
        Assert.False(first.Waited);
        await first.DisposeAsync();

        // The release is what makes a normal double-trigger merely serial rather than fatal: the next execution
        // takes the tables cleanly once the holder ends, on success or failure.
        await using var second = await WorkTableLease.AcquireAsync(cs, staging, "flow_under_test", 0, CancellationToken.None);
        Assert.Equal(first.Resource, second.Resource);
    }

    [Fact]
    public void ResourceFor_IsPerWorkTable_AndCaseCanonical()
    {
        var lower = WorkTableLease.ResourceFor(Staging("arc_orders_42"));
        var upper = WorkTableLease.ResourceFor(new RelationalObject { Database = "db", Schema = "RAW", Name = "ARC_Orders_42" });
        var other = WorkTableLease.ResourceFor(Staging("arc_orders_43"));

        // A case-insensitive server has one table here, so it must have one lease: two spellings of one name may
        // never yield two resources, and two flows may never collapse into one.
        Assert.Equal(lower, upper);
        Assert.NotEqual(lower, other);
        Assert.StartsWith("SqlFlow.WorkTables:", lower, StringComparison.Ordinal);
        // Application-lock resources are capped at 255 characters; a work-table name is capped at 128 plus a short
        // schema, so the composed resource always fits.
        Assert.True(lower.Length <= 255);
    }
}
