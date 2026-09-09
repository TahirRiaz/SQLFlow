using SqlFlow.Core;
using SqlFlow.Core.Ingestion.Legacy;
using Xunit;

namespace SqlFlow.Tests;

public sealed class SurrogateKeyMappingTests
{
    private static LegacyIngestionRow Flow() => new()
    {
        FlowID = 3,
        srcServer = "s",
        srcDBSchTbl = "[d].[dbo].[Src]",
        trgServer = "t",
        trgDBSchTbl = "[d].[dbo].[Trg]",
    };

    [Fact]
    public void Maps_SurrogateKey_WithDefaults()
    {
        var sk = new LegacySurrogateKeyRow { SurrogateKeyID = 9, FlowID = 3, SurrogateDbSchTbl = "[DW].[dbo].[DimCust]", SurrogateColumn = "CustKey", KeyColumns = "CustId,SrcSys" };

        var flow = IngestionFlowMapper.FromLegacy(Flow(), null, [sk]);

        var spec = Assert.Single(flow.SurrogateKeys);
        Assert.Equal(9, spec.SurrogateKeyId);
        Assert.Equal("CustKey", spec.SurrogateColumn);
        Assert.Equal(new[] { "CustId", "SrcSys" }, spec.KeyColumns);
        Assert.Empty(spec.SKeyColumns);
        Assert.Null(spec.ConnectionReference);
        Assert.Equal("DW", spec.SurrogateTable.Database);
        Assert.Equal("DimCust", spec.SurrogateTable.Name);
    }

    [Fact]
    public void Maps_SKeyColumns_AndRemoteServer()
    {
        var sk = new LegacySurrogateKeyRow { SurrogateKeyID = 1, FlowID = 3, SurrogateServer = "prod", SurrogateDbSchTbl = "[DW].[dbo].[Dim]", SurrogateColumn = "K", KeyColumns = "A,B", sKeyColumns = "X,Y" };

        var spec = Assert.Single(IngestionFlowMapper.FromLegacy(Flow(), null, [sk]).SurrogateKeys);

        Assert.Equal("@prod", spec.ConnectionReference);
        Assert.Equal(new[] { "X", "Y" }, spec.SKeyColumns);
    }

    [Fact]
    public void MissingKeyColumns_Throws()
    {
        var sk = new LegacySurrogateKeyRow { SurrogateKeyID = 1, FlowID = 3, SurrogateDbSchTbl = "[DW].[dbo].[Dim]", SurrogateColumn = "K" };
        Assert.Throws<SqlFlowException>(() => IngestionFlowMapper.FromLegacy(Flow(), null, [sk]));
    }

    [Fact]
    public void OnlyMatchingFlowId_IsMapped()
    {
        var sk = new LegacySurrogateKeyRow { SurrogateKeyID = 1, FlowID = 99, SurrogateDbSchTbl = "[DW].[dbo].[Dim]", SurrogateColumn = "K", KeyColumns = "A" };
        Assert.Empty(IngestionFlowMapper.FromLegacy(Flow(), null, [sk]).SurrogateKeys);
    }
}
