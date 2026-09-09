using SqlFlow.SqlServer.Schema;
using Xunit;

namespace SqlFlow.Tests;

public sealed class RewritePolicyTests
{
    [Fact]
    public void OptIn_AllowsInline()
        => Assert.Equal(RewriteHandling.Inline, RewritePolicy.Decide(allowTableRewrite: true));

    [Fact]
    public void NoOptIn_RequiresOptIn()
        => Assert.Equal(RewriteHandling.RequireOptIn, RewritePolicy.Decide(allowTableRewrite: false));
}
