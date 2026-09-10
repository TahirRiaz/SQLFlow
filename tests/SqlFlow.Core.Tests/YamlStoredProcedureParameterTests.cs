using SqlFlow.Core;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The stored-procedure parameters surface (procedure.parameters), the V3 form of the legacy flw.Parameter
/// table: a value is either a literal or a scalar query resolved at run time, a prefetched parameter declares
/// its resolution order, and a query may name an alternate server. The failure cases matter as much as the
/// happy path, because a silently-ignored parameter block would validate green and then fail inside SQL Server.
/// </summary>
public sealed class YamlStoredProcedureParameterTests
{
    private static readonly YamlStoredProcedureFlowLoader Loader = new();

    private static string Document(string parameters) => $$"""
        flowType: sp
        name: stage-fact
        connections:
          dwh: ${env:DWH}
          other: ${env:OTHER}
        procedure:
          server: dwh
          object: DW.arc.usp_Stage
          parameters:
        {{parameters}}
        """;

    [Fact]
    public void NoParametersBlock_YieldsEmptyList()
    {
        var flow = Loader.Parse("""
            flowType: sp
            name: stage-fact
            connections:
              dwh: ${env:DWH}
            procedure:
              server: dwh
              object: DW.arc.usp_Stage
            """).Flow;

        Assert.Empty(flow.Parameters);
    }

    [Fact]
    public void SelectExp_MapsAsRuntimeQuery()
    {
        var flow = Loader.Parse(Document("""
                UpdatedDate_DW:
                  selectExp: "SELECT ISNULL(MAX(UpdatedDate_DW),'1900-01-01') FROM edw.Fact"
            """)).Flow;

        var p = Assert.Single(flow.Parameters);
        Assert.Equal("UpdatedDate_DW", p.Name);
        Assert.Equal("SELECT ISNULL(MAX(UpdatedDate_DW),'1900-01-01') FROM edw.Fact", p.SelectExp);
        Assert.Null(p.Value);
        Assert.Null(p.Server);
        Assert.False(p.Prefetch);
    }

    [Fact]
    public void ScalarShorthand_IsALiteralValue()
    {
        var flow = Loader.Parse(Document("""
                BatchSize: 5000
                Label: nightly
            """)).Flow;

        Assert.Collection(flow.Parameters,
            p => { Assert.Equal("BatchSize", p.Name); Assert.Equal(5000, Convert.ToInt32(p.Value, System.Globalization.CultureInfo.InvariantCulture)); Assert.Null(p.SelectExp); },
            p => { Assert.Equal("Label", p.Name); Assert.Equal("nightly", p.Value); Assert.Null(p.SelectExp); });
    }

    [Fact]
    public void LeadingAtSign_IsOptionalAndNormalised()
    {
        var flow = Loader.Parse(Document("""
                "@UpdatedDate_DW":
                  value: 2020-01-01
            """)).Flow;

        Assert.Equal("UpdatedDate_DW", Assert.Single(flow.Parameters).Name);
    }

    [Fact]
    public void DuplicateName_DifferingOnlyByAtSign_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(Document("""
                UpdatedDate_DW:
                  value: 1
                "@UpdatedDate_DW":
                  value: 2
            """)));

        Assert.Contains("more than once", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PrefetchAndAlternateServer_Map()
    {
        var flow = Loader.Parse(Document("""
                Cutoff:
                  selectExp: SELECT MAX(Dat) FROM stg.Control
                  server: other
                  prefetch: true
                  default: 1900-01-01
            """)).Flow;

        var p = Assert.Single(flow.Parameters);
        Assert.True(p.Prefetch);
        Assert.Equal("other", p.Server);
        Assert.NotNull(p.Default);
    }

    [Fact]
    public void PrefetchedParameters_ResolveBeforeTheRest()
    {
        // Declaration order is preserved; the runner is what reorders, so the loader must not lose either flag.
        var flow = Loader.Parse(Document("""
                Plain:
                  selectExp: SELECT 1
                Early:
                  selectExp: SELECT 2
                  prefetch: true
            """)).Flow;

        Assert.Equal(["Plain", "Early"], flow.Parameters.Select(p => p.Name));
        Assert.False(flow.Parameters[0].Prefetch);
        Assert.True(flow.Parameters[1].Prefetch);
    }

    [Fact]
    public void NeitherSelectExpNorValue_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(Document("""
                Cutoff:
                  prefetch: true
            """)));

        Assert.Contains("must declare either", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BothSelectExpAndValue_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(Document("""
                Cutoff:
                  selectExp: SELECT 1
                  value: 2
            """)));

        Assert.Contains("use exactly one", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownConnectionOnParameter_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(Document("""
                Cutoff:
                  selectExp: SELECT 1
                  server: nope
            """)));

        Assert.Contains("not declared in 'connections'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ServerWithoutSelectExp_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(Document("""
                Cutoff:
                  value: 1
                  server: other
            """)));

        Assert.Contains("has no 'selectExp'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownParameterKey_Fails()
    {
        // The document deserializer ignores unmatched properties, so a typo inside a parameter would otherwise
        // be silently dropped and the run would fail deep in SQL Server. This block validates strictly.
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(Document("""
                Cutoff:
                  select_exp: SELECT 1
            """)));

        Assert.Contains("unknown key", ex.Message, StringComparison.Ordinal);
    }
}
