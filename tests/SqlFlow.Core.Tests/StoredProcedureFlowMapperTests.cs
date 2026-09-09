using SqlFlow.Core;
using SqlFlow.Core.StoredProcedures.Legacy;
using Xunit;

namespace SqlFlow.Tests;

public sealed class StoredProcedureFlowMapperTests
{
    private static LegacyStoredProcedureRow Row() => new()
    {
        FlowID = 5,
        SysAlias = "sys",
        trgServer = "dw",
        trgDBSchSP = "[DW].[dbo].[usp_Refresh]",
    };

    [Fact]
    public void Maps_RequiredFields_AndDefaults()
    {
        var flow = StoredProcedureFlowMapper.FromLegacy(Row());

        Assert.Equal(5, flow.FlowId);
        Assert.Equal("dw", flow.Server);
        Assert.Equal("@dw", flow.ConnectionReference);
        Assert.Equal("DW", flow.Procedure.Database);
        Assert.Equal("dbo", flow.Procedure.Schema);
        Assert.Equal("usp_Refresh", flow.Procedure.Name);
        Assert.True(flow.OnErrorResume);
        Assert.Equal("sp", flow.FlowType);
    }

    [Fact]
    public void MissingTrgServer_Throws()
    {
        var row = Row();
        row.trgServer = null;
        Assert.Throws<SqlFlowException>(() => StoredProcedureFlowMapper.FromLegacy(row));
    }

    [Fact]
    public void MissingProcedure_Throws()
    {
        var row = Row();
        row.trgDBSchSP = " ";
        Assert.Throws<SqlFlowException>(() => StoredProcedureFlowMapper.FromLegacy(row));
    }

    [Fact]
    public void TwoPartProcedure_Throws()
    {
        var row = Row();
        row.trgDBSchSP = "[dbo].[usp_X]";
        Assert.Throws<SqlFlowException>(() => StoredProcedureFlowMapper.FromLegacy(row));
    }
}
