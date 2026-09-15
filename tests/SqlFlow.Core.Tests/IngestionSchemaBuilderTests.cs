using SqlFlow.Core;
using SqlFlow.Core.Ingestion;
using SqlFlow.SqlServer.Schema;
using Xunit;

namespace SqlFlow.Tests;

public sealed class IngestionSchemaBuilderTests
{
    private static readonly IngestionSchemaBuilder Builder = new(new DefaultColumnNameCleaner());

    private static SqlColumn Src(string name, string type, bool nullable = true)
        => new() { Name = name, DataType = SqlDataType.Parse(type), IsNullable = nullable };

    private static IngestionFlow Flow(
        SchemaSyncPolicy? sync = null,
        SystemColumnsPolicy? system = null,
        ChangePolicy? change = null,
        IReadOnlyList<VirtualColumn>? virtuals = null,
        string? identity = null) => new()
    {
        FlowId = 1,
        Source = new IngestionSource { Server = "src", Table = RelationalObject.Parse("[Db].[dbo].[Src]") },
        Target = new IngestionTarget { Server = "dwh", Table = RelationalObject.Parse("[Db].[dbo].[Trg]"), IdentityColumn = identity },
        SchemaSync = sync ?? new SchemaSyncPolicy(),
        SystemColumns = system ?? new SystemColumnsPolicy { InsertedDate = false, UpdatedDate = false },
        Change = change ?? new ChangePolicy(),
        VirtualColumns = virtuals ?? [],
    };

    private static SqlColumn Col(DesiredSchema schema, string name)
        => schema.Columns.Single(c => c.Name == name);

    [Fact]
    public void SystemColumns_InjectedAndOrderedLast()
    {
        var schema = Builder.Build(
            [Src("Id", "int", nullable: false), Src("Name", "nvarchar(50)")],
            Flow(system: new SystemColumnsPolicy()), // defaults: Inserted + Updated
            forStaging: true);

        Assert.Equal(new[] { "Id", "Name", "InsertedDate_DW", "UpdatedDate_DW" }, schema.Columns.Select(c => c.Name).ToArray());
        // Audit stamps are datetime (not datetime2), matching the original SQLFlow arc/ods tables.
        Assert.Equal("datetime", Col(schema, "InsertedDate_DW").DataType.Render());
        Assert.Equal(ColumnOrigin.Computed, Col(schema, "InsertedDate_DW").Origin);
        Assert.Equal(ColumnOrigin.BulkCopied, Col(schema, "Id").Origin);
        Assert.Equal(new[] { "Id", "Name" }, schema.SourceToTargetNames.Keys.OrderBy(k => k).ToArray());
    }

    [Fact]
    public void HashKey_InjectedWithAlgorithmSizedType()
    {
        var schema = Builder.Build(
            [Src("Id", "int")],
            Flow(change: new ChangePolicy { HashColumns = ["Id"] }),
            forStaging: true);

        var hash = Col(schema, "HashKey_DW");
        Assert.Equal("binary(32)", hash.DataType.Render()); // SHA2_256 default
        Assert.Equal(ColumnRole.HashKey, hash.Role);
        Assert.False(schema.SourceToTargetNames.ContainsKey("HashKey_DW"));
    }

    [Fact]
    public void Identity_InjectedOnTargetOnly()
    {
        var target = Builder.Build([Src("Name", "nvarchar(50)")], Flow(identity: "Sk"), forStaging: false);
        var sk = Col(target, "Sk");
        Assert.True(sk.IsIdentity);
        Assert.True(sk.IsPrimaryKey);
        Assert.False(sk.IsNullable);
        Assert.Equal("int", sk.DataType.Render());

        var staging = Builder.Build([Src("Name", "nvarchar(50)")], Flow(identity: "Sk"), forStaging: true);
        Assert.DoesNotContain(staging.Columns, c => c.Name == "Sk");
    }

    [Fact]
    public void UnicodeConversion_AppliesToSourceTypes()
    {
        var schema = Builder.Build(
            [Src("Name", "nvarchar(50)")],
            Flow(sync: new SchemaSyncPolicy { ConvertUnicodeToNonUnicode = true }),
            forStaging: true);

        Assert.Equal("varchar(50)", Col(schema, "Name").DataType.Render());
    }

    [Fact]
    public void Cleanup_RenamesSourceColumns_AndMapsThem()
    {
        var schema = Builder.Build(
            [Src("Order No", "int")],
            Flow(sync: new SchemaSyncPolicy { CleanColumnNames = true, CleanColumnNameRegex = "[^A-Za-z0-9_]" }),
            forStaging: true);

        Assert.Contains(schema.Columns, c => c.Name == "OrderNo");
        Assert.Equal("OrderNo", schema.SourceToTargetNames["Order No"]);
    }

    [Fact]
    public void VirtualColumn_ReplacingASourceColumn_ReadsItsExpressionUnderTheSourceName()
    {
        var schema = Builder.Build(
            [Src("Id", "int"), Src("LoadTag", "nvarchar(10)")],
            Flow(virtuals: [new VirtualColumn { Name = "LoadTag", SelectExpression = "'X'" }]),
            forStaging: true);

        var v = Col(schema, "LoadTag");
        Assert.Equal(ColumnRole.Virtual, v.Role);
        Assert.Equal(ColumnOrigin.BulkCopied, v.Origin);
        Assert.Equal("'X'", v.SelectExpression);
        Assert.Equal("nvarchar(10)", v.DataType.Render()); // undeclared: keeps the source column's type
        Assert.Equal("LoadTag", schema.SourceToTargetNames["LoadTag"]);
        Assert.Equal(new[] { "Id", "LoadTag" }, schema.Projections.Select(p => p.SourceName).ToArray());
        Assert.Null(schema.Projections[0].Expression);
        Assert.Equal("'X'", schema.Projections[1].Expression);
    }

    [Fact]
    public void VirtualColumn_ReplacingASourceColumn_TakesTheDeclaredType()
    {
        var schema = Builder.Build(
            [Src("Code", "varchar(20)")],
            Flow(virtuals: [new VirtualColumn { Name = "Code", DataTypeExpression = "int", SelectExpression = "CAST([Code] AS int)" }]),
            forStaging: true);

        Assert.Equal("int", Col(schema, "Code").DataType.Render());
    }

    [Fact]
    public void VirtualColumn_NotInSource_IsAppendedAsADataColumn_WithItsDeclaredType()
    {
        // The legacy row shape: a bracketed name, DataType holding the family and DataTypeExp the full type.
        var schema = Builder.Build(
            [Src("VehicleIdentity", "bigint"), Src("PlannedArrivalTime", "datetime")],
            Flow(virtuals:
            [
                new VirtualColumn { Name = "[ArrivalPlan_DW]", DataType = "time", DataTypeExpression = "time(0)", SelectExpression = "CAST([PlannedArrivalTime] AS TIME)" },
                new VirtualColumn { Name = "Tag", DataType = "varchar(5)", SelectExpression = "'a'" },
            ]),
            forStaging: true);

        var arrival = Col(schema, "ArrivalPlan_DW");
        Assert.Equal("time(0)", arrival.DataType.Render());
        Assert.True(arrival.IsNullable);
        Assert.Equal(ColumnRole.Virtual, arrival.Role);
        Assert.Equal(ColumnOrigin.BulkCopied, arrival.Origin);
        Assert.Equal("varchar(5)", Col(schema, "Tag").DataType.Render());
        Assert.Equal(new[] { "VehicleIdentity", "PlannedArrivalTime", "ArrivalPlan_DW", "Tag" }, schema.Projections.Select(p => p.SourceName).ToArray());
        Assert.Equal("CAST([PlannedArrivalTime] AS TIME)", schema.Projections[2].Expression);
        Assert.Equal("ArrivalPlan_DW", schema.SourceToTargetNames["ArrivalPlan_DW"]);
        // The schema keeps the legacy order: _DW columns last, so the appended Tag precedes ArrivalPlan_DW.
        Assert.Equal(new[] { "VehicleIdentity", "PlannedArrivalTime", "Tag", "ArrivalPlan_DW" }, schema.Columns.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void VirtualColumn_NotInSource_WithoutAType_Fails()
    {
        var ex = Assert.Throws<SqlFlowException>(() => Builder.Build(
            [Src("Id", "int")],
            Flow(virtuals: [new VirtualColumn { Name = "Tag", SelectExpression = "'a'" }]),
            forStaging: true));

        Assert.Contains("Virtual column 'Tag' is not a column of source", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void VirtualColumn_WithoutAName_Fails(string? name)
    {
        var ex = Assert.Throws<SqlFlowException>(() => Builder.Build(
            [Src("Id", "int")],
            Flow(virtuals: [new VirtualColumn { Name = name, DataType = "int", SelectExpression = "1" }]),
            forStaging: true));

        Assert.Contains("has no name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VirtualColumn_DeclaredTwice_Fails()
    {
        var ex = Assert.Throws<SqlFlowException>(() => Builder.Build(
            [Src("Id", "int")],
            Flow(virtuals:
            [
                new VirtualColumn { Name = "Tag", DataType = "int", SelectExpression = "1" },
                new VirtualColumn { Name = "[tag]", DataType = "int", SelectExpression = "2" },
            ]),
            forStaging: true));

        Assert.Contains("declared more than once", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VirtualColumn_UnparseableType_FailsNamingTheKey()
    {
        var ex = Assert.Throws<SqlFlowException>(() => Builder.Build(
            [Src("Id", "int")],
            Flow(virtuals: [new VirtualColumn { Name = "Tag", DataTypeExpression = "CAST('2022-01-01' AS DATE)", SelectExpression = "1" }]),
            forStaging: true));

        Assert.Contains("declares dataTypeExpression 'CAST('2022-01-01' AS DATE)'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VirtualColumn_NamedLikeAnEnabledSystemColumn_Fails()
    {
        var ex = Assert.Throws<SqlFlowException>(() => Builder.Build(
            [Src("Id", "int")],
            Flow(
                system: new SystemColumnsPolicy(),
                virtuals: [new VirtualColumn { Name = "UpdatedDate_DW", DataType = "datetime", SelectExpression = "GETDATE()" }]),
            forStaging: true));

        Assert.Contains("engine-maintained column 'UpdatedDate_DW'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordering_PkPrefixFirst_DwLast()
    {
        var schema = Builder.Build(
            [Src("Name", "nvarchar(50)"), Src("PKId", "int"), Src("CustomerPK", "int")],
            Flow(system: new SystemColumnsPolicy()), // Inserted + Updated _DW
            forStaging: true);

        Assert.Equal(
            new[] { "PKId", "CustomerPK", "Name", "InsertedDate_DW", "UpdatedDate_DW" },
            schema.Columns.Select(c => c.Name).ToArray());
    }
}
