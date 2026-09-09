using SqlFlow.Core;
using SqlFlow.Core.Invoke;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>The invoke hooks across the document kinds: an <c>invokes:</c> block plus preInvoke/postInvoke in
/// the ing/exp/sp documents reaches the model, and a hook referencing an undeclared invoke fails at parse time
/// with the YAML path.</summary>
public sealed class YamlInvokeHooksTests
{
    private const string InvokeBlocks = """
        servicePrincipals:
          deploy:
            subscriptionId: s-1
            resourceGroup: rg-1
            dataFactoryName: adf-1
        invokes:
          refresh-marts:
            type: adf
            pipeline: pl_refresh
            servicePrincipal: deploy
        """;

    [Fact]
    public void Ingestion_WiresPreAndPostInvoke()
    {
        var doc = new YamlIngestionFlowLoader().Parse($$"""
            flowType: ing
            name: orders
            connections:
              sink: ${env:SINK}
            source:
              server: sink
              object: db.dbo.Src
            target:
              server: sink
              object: db.dbo.Trg
            preInvoke: refresh-marts
            postInvoke: refresh-marts
            {{InvokeBlocks}}
            """);

        Assert.Equal("refresh-marts", doc.Flow.Process.PreInvokeAlias);
        Assert.Equal("refresh-marts", doc.Flow.Process.PostInvokeAlias);
        var invoke = Assert.Single(doc.Invokes);
        Assert.Equal(InvokeType.AzureDataFactory, invoke.InvokeType);
        Assert.Equal("@deploy", invoke.TargetServicePrincipalReference);
        Assert.Equal("deploy", Assert.Single(doc.ServicePrincipals).Alias);
    }

    [Fact]
    public void Export_WiresPostInvoke()
    {
        var doc = new YamlExportFlowLoader().Parse($$"""
            flowType: exp
            name: orders-export
            connections:
              sink: ${env:SINK}
            source:
              server: sink
              object: db.dbo.Src
            target:
              path: ./out
            postInvoke: refresh-marts
            {{InvokeBlocks}}
            """);

        Assert.Equal("refresh-marts", doc.Flow.PostInvokeAlias);
        Assert.Single(doc.Invokes);
    }

    [Fact]
    public void StoredProcedure_WiresPostInvoke()
    {
        var doc = new YamlStoredProcedureFlowLoader().Parse($$"""
            flowType: sp
            name: refresh
            connections:
              sink: ${env:SINK}
            procedure:
              server: sink
              object: db.dbo.usp_X
            postInvoke: refresh-marts
            {{InvokeBlocks}}
            """);

        Assert.Equal("refresh-marts", doc.Flow.PostInvokeAlias);
        Assert.Single(doc.Invokes);
    }

    [Fact]
    public void UndeclaredHookReference_FailsAtParseTime()
    {
        var ex = Assert.Throws<FlowValidationException>(() => new YamlStoredProcedureFlowLoader().Parse("""
            flowType: sp
            name: refresh
            connections:
              sink: ${env:SINK}
            procedure:
              server: sink
              object: db.dbo.usp_X
            postInvoke: ghost
            """));

        Assert.Contains("'postInvoke' references 'ghost', which is not declared under 'invokes:'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HooksOmitted_StayNull_SoTheNoOpDefaultApplies()
    {
        var doc = new YamlIngestionFlowLoader().Parse("""
            flowType: ing
            name: orders
            connections:
              sink: ${env:SINK}
            source: { server: sink, object: db.dbo.Src }
            target: { server: sink, object: db.dbo.Trg }
            """);

        Assert.Null(doc.Flow.Process.PreInvokeAlias);
        Assert.Null(doc.Flow.Process.PostInvokeAlias);
        Assert.Empty(doc.Invokes);
        Assert.Empty(doc.ServicePrincipals);
    }
}
