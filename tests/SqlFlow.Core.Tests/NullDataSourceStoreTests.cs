using SqlFlow.Core;
using SqlFlow.Core.Connections;
using Xunit;

namespace SqlFlow.Tests;

public sealed class NullDataSourceStoreTests
{
    [Fact]
    public void SupportsAliases_IsFalse()
        => Assert.False(new NullDataSourceStore().SupportsAliases);

    [Fact]
    public async Task ResolveAsync_Throws_WithGuidance()
    {
        var store = new NullDataSourceStore();
        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => store.ResolveAsync("dwh"));
        Assert.Contains("lightweight mode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
