using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using Xunit;

namespace SqlFlow.Tests;

public sealed class SecretResolverTests
{
    private static readonly ISecretResolver Resolver = new SecretResolver([new EnvSecretProvider()]);

    [Fact]
    public void Resolve_PlainValue_Unchanged()
        => Assert.Equal("Server=localhost;Database=DW", Resolver.Resolve("Server=localhost;Database=DW"));

    [Fact]
    public void Resolve_EnvReference_Expanded()
    {
        Environment.SetEnvironmentVariable("SQLFLOW_TEST_SECRET", "resolved-value");
        try
        {
            Assert.Equal("x=resolved-value;", Resolver.Resolve("x=${env:SQLFLOW_TEST_SECRET};"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SQLFLOW_TEST_SECRET", null);
        }
    }

    [Fact]
    public void Resolve_UnknownScheme_Throws()
        => Assert.Throws<SqlFlowException>(() => Resolver.Resolve("${vault:my/secret}"));

    [Fact]
    public void Resolve_MissingEnvVar_Throws()
        => Assert.Throws<SqlFlowException>(() => Resolver.Resolve("${env:SQLFLOW_DEFINITELY_MISSING_VAR}"));
}
