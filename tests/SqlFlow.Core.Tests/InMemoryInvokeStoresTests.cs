using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Invoke;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>The in-memory invoke and service-principal stores: the YAML-mode seams behind the same contracts
/// full mode uses (case-insensitive alias lookup, the batch filter, the registration-time secretless gate).</summary>
public sealed class InMemoryInvokeStoresTests
{
    private static InvokeDefinition Invoke(string alias, int flowId, string? batch = null, bool deactivated = false) => new()
    {
        FlowId = flowId,
        InvokeAlias = alias,
        InvokeType = InvokeType.AzureDataFactory,
        PipelineName = "pl",
        Batch = batch,
        DeactivateFromBatch = deactivated,
        TargetServicePrincipalReference = "@sp",
    };

    [Fact]
    public async Task InvokeStore_ResolvesByAliasCaseInsensitively_AndById()
    {
        var store = new InMemoryInvokeFlowStore([Invoke("Refresh", 7)]);

        Assert.Equal(7, (await store.LoadByAliasAsync("refresh")).FlowId);
        Assert.Equal("Refresh", (await store.LoadByIdAsync(7)).InvokeAlias);
    }

    [Fact]
    public async Task InvokeStore_UnknownAlias_NamesTheInvokesBlock()
    {
        var store = new InMemoryInvokeFlowStore([]);

        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => store.LoadByAliasAsync("ghost"));
        Assert.Contains("Declare it under 'invokes:' in the flow document.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvokeStore_DuplicateAlias_Throws()
    {
        var ex = Assert.Throws<SqlFlowException>(() => new InMemoryInvokeFlowStore([Invoke("x", 1), Invoke("X", 2)]));
        Assert.Contains("Duplicate invoke name 'X'.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeStore_Batch_FiltersDeactivated_AndOrdersByFlowId()
    {
        var store = new InMemoryInvokeFlowStore(
        [
            Invoke("b", 2, batch: "nightly"),
            Invoke("a", 1, batch: "nightly"),
            Invoke("off", 3, batch: "nightly", deactivated: true),
            Invoke("other", 4, batch: "hourly"),
        ]);

        var flows = await store.LoadBatchAsync("NIGHTLY");

        Assert.Equal([1, 2], flows.Select(f => f.FlowId).ToArray());
    }

    [Fact]
    public async Task SpStore_Resolves_AndReportsAliasSupport()
    {
        var store = new InMemoryServicePrincipalStore(
        [
            new ServicePrincipalProfile { Alias = "deploy", SubscriptionId = "s", ResourceGroup = "rg", ClientSecretRef = "${env:S}" },
        ]);

        Assert.True(store.SupportsAliases);
        Assert.Equal("rg", (await store.ResolveAsync("DEPLOY")).ResourceGroup);
        await Assert.ThrowsAsync<ServicePrincipalNotFoundException>(() => store.ResolveAsync("ghost"));
    }

    [Fact]
    public void SpStore_RestingSecret_IsRejectedAtRegistration()
    {
        Assert.Throws<SecretlessViolationException>(() => new InMemoryServicePrincipalStore(
        [
            new ServicePrincipalProfile { Alias = "deploy", ClientSecretRef = "hunter2" },
        ]));
    }
}
