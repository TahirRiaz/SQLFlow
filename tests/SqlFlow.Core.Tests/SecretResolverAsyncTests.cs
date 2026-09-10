using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using Xunit;

namespace SqlFlow.Tests;

public sealed class SecretResolverAsyncTests
{
    private static readonly ISecretResolver Resolver = new SecretResolver([new EnvSecretProvider()]);

    [Fact]
    public async Task ResolveAsync_PlainValue_Unchanged()
        => Assert.Equal("Server=localhost;Database=DW", await Resolver.ResolveAsync("Server=localhost;Database=DW"));

    [Fact]
    public async Task ResolveAsync_MultipleReferences_AllExpanded()
    {
        Environment.SetEnvironmentVariable("SQLFLOW_TEST_ASYNC_SECRET", "R");
        try
        {
            Assert.Equal(
                "a=R;b=R;tail",
                await Resolver.ResolveAsync("a=${env:SQLFLOW_TEST_ASYNC_SECRET};b=${env:SQLFLOW_TEST_ASYNC_SECRET};tail"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SQLFLOW_TEST_ASYNC_SECRET", null);
        }
    }

    [Fact]
    public async Task ResolveAsync_UnknownScheme_Throws()
        => await Assert.ThrowsAsync<SqlFlowException>(() => Resolver.ResolveAsync("${vault:my/secret}"));

    [Fact]
    public async Task ResolveAsync_Null_Throws()
        => await Assert.ThrowsAsync<ArgumentNullException>(() => Resolver.ResolveAsync(null!));
}
