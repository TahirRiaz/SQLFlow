using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The YAML surface of the landing reset: <c>load.resetWhenConsolidated</c> is ON by default (a chained landing
/// table is staging, and staging that never resets grows without bound), and the key exists only as an explicit
/// per-flow opt-out for a landing table whose accumulated rows something still depends on.
/// </summary>
public sealed class LandingResetYamlTests
{
    private static string Doc(string load) => """
        name: t
        source:
          type: csv
          location: /data/x.csv
        target:
          connection: ${env:CS}
          schema: pre
          table: T
        __LOAD__
        """.Replace("__LOAD__", load, StringComparison.Ordinal);

    [Fact]
    public void Default_IsOn_EvenWithNoLoadBlock()
    {
        var flow = new YamlFlowLoader().Parse(Doc(string.Empty));
        Assert.True(flow.Load.ResetWhenConsolidated);
    }

    [Fact]
    public void Default_IsOn_WhenTheLoadBlockDoesNotMentionIt()
    {
        var flow = new YamlFlowLoader().Parse(Doc("""
            load:
              mode: append
            """));
        Assert.True(flow.Load.ResetWhenConsolidated);
    }

    [Fact]
    public void ExplicitFalse_OptsTheFlowOut()
    {
        var flow = new YamlFlowLoader().Parse(Doc("""
            load:
              mode: append
              resetWhenConsolidated: false
            """));
        Assert.False(flow.Load.ResetWhenConsolidated);
    }
}
