using SqlFlow.Core;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Ingestion.Legacy;
using Xunit;

namespace SqlFlow.Tests;

public sealed class IngestionFlowMapperTests
{
    private static LegacyIngestionRow MinimalRow() => new()
    {
        FlowID = 42,
        srcServer = "crm",
        srcDBSchTbl = "[SrcDb].[dbo].[Customer]",
        trgServer = "dwh",
        trgDBSchTbl = "[DwDb].[stg].[Customer]",
    };

    [Fact]
    public void Minimal_AppliesLegacyDefaults()
    {
        var flow = IngestionFlowMapper.FromLegacy(MinimalRow());

        Assert.Equal(42, flow.FlowId);
        Assert.Equal("ing", flow.FlowType);
        Assert.True(flow.OnErrorResume);
        Assert.False(flow.DeactivateFromBatch);

        Assert.Equal("crm", flow.Source.Server);
        Assert.Equal("@crm", flow.Source.ConnectionReference);
        Assert.Equal("SrcDb", flow.Source.Table.Database);
        Assert.Equal("dbo", flow.Source.Table.Schema);
        Assert.Equal("Customer", flow.Source.Table.Name);
        Assert.True(flow.Source.FilterIsAppend);

        Assert.Equal("@dwh", flow.Target.ConnectionReference);
        Assert.Equal("stg", flow.Target.Table.Schema);

        Assert.True(flow.Load.StreamData);
        Assert.Null(flow.Load.Threads);
        Assert.Equal(2000, flow.Load.BatchUpsertRowCount);
        Assert.Empty(flow.Load.KeyColumns);

        Assert.True(flow.SchemaSync.Sync);
        Assert.Equal(7, flow.Incremental.OverlapDays);
        Assert.False(flow.Incremental.FullLoad);

        Assert.True(flow.SystemColumns.InsertedDate);
        Assert.True(flow.SystemColumns.UpdatedDate);
        Assert.False(flow.SystemColumns.DeletedDate);
        Assert.False(flow.SystemColumns.RowStatus);

        Assert.Equal(new[] { "CheckEmptyTable", "CheckFreshnessDaily" }, flow.Assertions);
        Assert.Empty(flow.VirtualColumns);
        Assert.False(flow.Change.HasHashKey);
    }

    [Fact]
    public void Lists_AreParsed_BracketAndWhitespaceTolerant()
    {
        var row = MinimalRow();
        row.KeyColumns = "[Customer Id], Region ,[Sub]";
        row.IncrementalColumns = "UpdatedDate";
        row.HashKeyColumns = "Col1,Col2";
        row.IgnoreColumns = "Temp1, Temp2";

        var flow = IngestionFlowMapper.FromLegacy(row);

        Assert.Equal(new[] { "Customer Id", "Region", "Sub" }, flow.Load.KeyColumns);
        Assert.Equal(new[] { "UpdatedDate" }, flow.Incremental.Columns);
        Assert.True(flow.Change.HasHashKey);
        Assert.Equal(new[] { "Col1", "Col2" }, flow.Change.HashColumns);
        Assert.Equal(new[] { "Temp1", "Temp2" }, flow.Source.IgnoreColumns);
    }

    [Fact]
    public void FullLoad_IntIsTruthy_And_ZeroThreadsMeansDefault()
    {
        var row = MinimalRow();
        row.FullLoad = 3;     // non-zero means full
        row.NoOfThreads = 0;  // 0 means engine default (null)

        var flow = IngestionFlowMapper.FromLegacy(row);
        Assert.True(flow.Incremental.FullLoad);
        Assert.Null(flow.Load.Threads);

        row.FullLoad = 0;
        row.NoOfThreads = 4;
        var flow2 = IngestionFlowMapper.FromLegacy(row);
        Assert.False(flow2.Incremental.FullLoad);
        Assert.Equal(4, flow2.Load.Threads);
    }

    [Fact]
    public void SysColumns_ExplicitSet_TogglesExactlyThoseListed()
    {
        var row = MinimalRow();
        row.SysColumns = "InsertedDate_DW, DeletedDate_DW";

        var flow = IngestionFlowMapper.FromLegacy(row);
        Assert.True(flow.SystemColumns.InsertedDate);
        Assert.False(flow.SystemColumns.UpdatedDate);   // not listed, so off
        Assert.True(flow.SystemColumns.DeletedDate);
        Assert.False(flow.SystemColumns.RowStatus);
    }

    [Fact]
    public void SysColumns_EmptyString_MeansNone()
    {
        var row = MinimalRow();
        row.SysColumns = "";

        var flow = IngestionFlowMapper.FromLegacy(row);
        Assert.False(flow.SystemColumns.InsertedDate);
        Assert.False(flow.SystemColumns.UpdatedDate);
    }

    [Fact]
    public void SysColumns_Unknown_FailsFast_NamingTheToken()
    {
        var row = MinimalRow();
        row.SysColumns = "InsertedDate_DW,Bogus_DW";

        var ex = Assert.Throws<SqlFlowException>(() => IngestionFlowMapper.FromLegacy(row));
        Assert.Contains("Bogus_DW", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Assertions_EmptyString_MeansNone()
    {
        var row = MinimalRow();
        row.Assertions = "";
        Assert.Empty(IngestionFlowMapper.FromLegacy(row).Assertions);
    }

    [Fact]
    public void MissingSrcServer_FailsFast()
    {
        var row = MinimalRow();
        row.srcServer = null;

        var ex = Assert.Throws<SqlFlowException>(() => IngestionFlowMapper.FromLegacy(row));
        Assert.Contains("srcServer", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoPartTargetName_FailsFast_WithFlowAndColumn()
    {
        var row = MinimalRow();
        row.trgDBSchTbl = "dbo.Customer";

        var ex = Assert.Throws<SqlFlowException>(() => IngestionFlowMapper.FromLegacy(row));
        Assert.Contains("42", ex.Message, StringComparison.Ordinal);
        Assert.Contains("trgDBSchTbl", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VirtualColumns_FilteredByFlowId_AndMapped()
    {
        var row = MinimalRow();
        var virtuals = new[]
        {
            new LegacyIngestionVirtualRow { VirtualID = 1, FlowID = 42, ColumnName = "LoadId", DataType = "int", SelectExp = "1" },
            new LegacyIngestionVirtualRow { VirtualID = 2, FlowID = 99, ColumnName = "Other", SelectExp = "2" },
        };

        var flow = IngestionFlowMapper.FromLegacy(row, virtuals);

        Assert.Single(flow.VirtualColumns);
        Assert.Equal("LoadId", flow.VirtualColumns[0].Name);
        Assert.Equal("int", flow.VirtualColumns[0].DataType);
        Assert.Equal("1", flow.VirtualColumns[0].SelectExpression);
    }

    [Fact]
    public void VirtualColumn_EmptySelectExp_FailsFast()
    {
        var row = MinimalRow();
        var virtuals = new[] { new LegacyIngestionVirtualRow { VirtualID = 5, FlowID = 42, ColumnName = "X", SelectExp = "  " } };

        Assert.Throws<SqlFlowException>(() => IngestionFlowMapper.FromLegacy(row, virtuals));
    }

    [Fact]
    public void Filters_Process_And_Versioning_Mapped()
    {
        var row = MinimalRow();
        row.srcFilter = "Region = 'NA'";
        row.srcFilterIsAppend = false;
        row.IncrementalClauseExp = "AND SystemID = 13";
        row.PreProcessOnTrg = "stg.PreClean";
        row.PostInvokeAlias = "adf-refresh";
        row.trgVersioning = true;

        var flow = IngestionFlowMapper.FromLegacy(row);

        Assert.Equal("Region = 'NA'", flow.Source.Filter);
        Assert.False(flow.Source.FilterIsAppend);
        Assert.Equal("AND SystemID = 13", flow.Source.IncrementalClause);
        Assert.Equal("stg.PreClean", flow.Process.PreProcessOnTarget);
        Assert.Equal("adf-refresh", flow.Process.PostInvokeAlias);
        // trgVersioning maps onto the first-class temporal policy, keeping the legacy engine's datetime2(0)
        // period precision so a control-DB-sourced flow reproduces the tables that engine produced.
        Assert.True(flow.Versioning.Temporal.Enabled);
        Assert.Equal(0, flow.Versioning.Temporal.PeriodPrecision);
        Assert.Equal("ver", flow.Versioning.Temporal.HistorySchema);
    }
}
