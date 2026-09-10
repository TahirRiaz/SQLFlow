using SqlFlow.Core;
using SqlFlow.Core.Invoke;
using SqlFlow.Core.Invoke.Legacy;
using Xunit;

namespace SqlFlow.Tests;

public sealed class InvokeFlowMappingTests
{
    private static LegacyInvokeRow Row() => new()
    {
        FlowID = 9,
        InvokeAlias = "RefreshAdf",
        InvokeType = "adf",
    };

    [Fact]
    public void Maps_RequiredFields_AndDefaults()
    {
        var def = InvokeFlowMapper.FromLegacy(Row());

        Assert.Equal(9, def.FlowId);
        Assert.Equal("RefreshAdf", def.InvokeAlias);
        Assert.Equal(InvokeType.AzureDataFactory, def.InvokeType);
        Assert.Equal("adf", def.FlowType);
        Assert.True(def.OnErrorResume);
        Assert.False(def.DeactivateFromBatch);
        Assert.Null(def.TargetServicePrincipalReference);
        Assert.Null(def.SourceServicePrincipalReference);
    }

    [Theory]
    [InlineData("adf", InvokeType.AzureDataFactory)]
    [InlineData("aut", InvokeType.AzureAutomation)]
    [InlineData("", InvokeType.AzureAutomation)]
    [InlineData(null, InvokeType.AzureAutomation)]
    public void ParsesSupportedInvokeTypes(string? code, InvokeType expected)
    {
        var row = Row();
        row.InvokeType = code;
        Assert.Equal(expected, InvokeFlowMapper.FromLegacy(row).InvokeType);
    }

    [Theory]
    [InlineData("ps")]
    [InlineData("cs")]
    public void RemovedHostExecutionTypes_Throw(string code)
    {
        var row = Row();
        row.InvokeType = code;
        var ex = Assert.Throws<SqlFlowException>(() => InvokeFlowMapper.FromLegacy(row));
        Assert.Contains("injection", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownInvokeType_Throws()
    {
        var row = Row();
        row.InvokeType = "wat";
        Assert.Throws<SqlFlowException>(() => InvokeFlowMapper.FromLegacy(row));
    }

    [Fact]
    public void MissingInvokeAlias_Throws()
    {
        var row = Row();
        row.InvokeAlias = "   ";
        Assert.Throws<SqlFlowException>(() => InvokeFlowMapper.FromLegacy(row));
    }

    [Fact]
    public void ServicePrincipalAliases_CollapseToSecretlessReferences()
    {
        var row = Row();
        row.trgServicePrincipalAlias = "sp-trg";
        row.srcServicePrincipalAlias = "sp-src";
        var def = InvokeFlowMapper.FromLegacy(row);
        Assert.Equal("@sp-trg", def.TargetServicePrincipalReference);
        Assert.Equal("@sp-src", def.SourceServicePrincipalReference);
    }

    [Fact]
    public void CarriesNamedResourceAndParameters()
    {
        var row = Row();
        row.InvokeType = "aut";
        row.PipelineName = "pl_refresh";
        row.RunbookName = "rb_refresh";
        row.ParameterJSON = "{\"a\":1}";
        row.OnErrorResume = false;
        row.DeactivateFromBatch = true;

        var def = InvokeFlowMapper.FromLegacy(row);
        Assert.Equal(InvokeType.AzureAutomation, def.InvokeType);
        Assert.Equal("pl_refresh", def.PipelineName);
        Assert.Equal("rb_refresh", def.RunbookName);
        Assert.Equal("{\"a\":1}", def.ParameterJson);
        Assert.False(def.OnErrorResume);
        Assert.True(def.DeactivateFromBatch);
    }
}
