using SqlFlow.Core;
using SqlFlow.Core.Dataverse;
using SqlFlow.Core.Model;
using SqlFlow.Dataverse;
using Xunit;

namespace SqlFlow.Tests.Dataverse;

/// <summary>
/// How a Dataverse table's metadata becomes the columns, types and request of one read: every type maps to a stable
/// target type, columns the Web API cannot return in a row are left out (and refused when named), the primary key and
/// the watermark column always land, and the watermark bound becomes an OData literal the service can compare.
/// </summary>
public sealed class DataverseReadPlanTests
{
    private static DataverseAttribute Attribute(string name, string typeName, int? maxLength = null, string? behaviour = null, string? attributeOf = null)
        => new()
        {
            LogicalName = name,
            AttributeType = typeName[..^"Type".Length],
            AttributeTypeName = typeName,
            IsValidForRead = true,
            IsValidODataAttribute = true,
            MaxLength = maxLength,
            DateTimeBehavior = behaviour,
            AttributeOf = attributeOf,
        };

    private static DataverseTableMetadata Table(params DataverseAttribute[] more)
        => new()
        {
            LogicalName = "account",
            EntitySetName = "accounts",
            PrimaryIdAttribute = "accountid",
            Attributes = [Attribute("accountid", "UniqueidentifierType"), .. more],
        };

    private static DataverseSourceOptions Options(params (string Key, string? Value)[] options)
        => DataverseSourceOptions.Parse(DataverseTesting.Spec(options));

    [Theory]
    [InlineData("UniqueidentifierType", null, null, "UNIQUEIDENTIFIER", "accountid2")]
    [InlineData("StringType", 160, null, "NVARCHAR(160)", "c")]
    [InlineData("StringType", null, null, "NVARCHAR(MAX)", "c")]
    [InlineData("MemoType", 1048576, null, "NVARCHAR(MAX)", "c")]
    [InlineData("MemoType", 2000, null, "NVARCHAR(2000)", "c")]
    [InlineData("IntegerType", null, null, "INT", "c")]
    [InlineData("PicklistType", null, null, "INT", "c")]
    [InlineData("StateType", null, null, "INT", "c")]
    [InlineData("StatusType", null, null, "INT", "c")]
    [InlineData("BigIntType", null, null, "BIGINT", "c")]
    [InlineData("DecimalType", null, null, "DECIMAL(23, 10)", "c")]
    [InlineData("MoneyType", null, null, "DECIMAL(19, 4)", "c")]
    [InlineData("DoubleType", null, null, "FLOAT", "c")]
    [InlineData("BooleanType", null, null, "BIT", "c")]
    [InlineData("DateTimeType", null, "UserLocal", "DATETIME2", "c")]
    [InlineData("DateTimeType", null, "TimeZoneIndependent", "DATETIME2", "c")]
    [InlineData("DateTimeType", null, "DateOnly", "DATE", "c")]
    [InlineData("EntityNameType", null, null, "NVARCHAR(128)", "c")]
    [InlineData("LookupType", null, null, "UNIQUEIDENTIFIER", "_c_value")]
    [InlineData("CustomerType", null, null, "UNIQUEIDENTIFIER", "_c_value")]
    [InlineData("OwnerType", null, null, "UNIQUEIDENTIFIER", "_c_value")]
    [InlineData("MultiSelectPicklistType", null, null, "NVARCHAR(4000)", "c")]
    [InlineData("ManagedPropertyType", null, null, "NVARCHAR(MAX)", "c")]
    [InlineData("SomeFutureType", null, null, "NVARCHAR(MAX)", "c")]
    public void Every_type_maps_to_a_stable_target_type(string typeName, int? maxLength, string? behaviour, string sqlType, string property)
    {
        var name = property == "accountid2" ? "accountid2" : "c";
        var column = DataverseReadPlan.ColumnOf(Attribute(name, typeName, maxLength, behaviour))!;

        Assert.Equal(sqlType, column.SqlType);
        Assert.Equal(property, column.Property);
        Assert.Equal(name, column.SelectName);
    }

    [Theory]
    [InlineData("ImageType", "thumbnail")]
    [InlineData("FileType", "file column")]
    [InlineData("PartyListType", "party list")]
    [InlineData("CalendarRulesType", "calendar rules")]
    [InlineData("VirtualType", "no stored value")]
    public void A_column_the_web_api_cannot_return_in_a_row_is_left_out_and_refused_when_named(string typeName, string reason)
    {
        var table = Table(Attribute("name", "StringType", 100), Attribute("c", typeName));

        Assert.DoesNotContain(DataverseReadPlan.Build(table, Options()).Columns, column => column.Name == "c");
        var refused = Assert.Throws<SqlFlowException>(() => DataverseReadPlan.Build(table, Options(("columns", "name,c"))));
        Assert.Contains(reason, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parts_of_other_columns_and_columns_not_valid_for_read_are_left_out()
    {
        var table = Table(
            Attribute("parentaccountid", "LookupType"),
            Attribute("parentaccountidname", "StringType", 160, attributeOf: "parentaccountid"),
            Attribute("hidden", "StringType", 10) with { IsValidForRead = false },
            Attribute("notodata", "StringType", 10) with { IsValidODataAttribute = false });

        Assert.Equal(["accountid", "parentaccountid"], DataverseReadPlan.Build(table, Options()).Columns.Select(c => c.Name));
    }

    [Fact]
    public void The_primary_key_lands_first_then_every_other_column_by_name()
    {
        var plan = DataverseReadPlan.Build(Table(Attribute("zeta", "IntegerType"), Attribute("alpha", "IntegerType")), Options());

        Assert.Equal(["accountid", "alpha", "zeta"], plan.Columns.Select(c => c.Name));
        Assert.Contains("$select=accountid,alpha,zeta&", plan.FirstPage.AbsoluteUri, StringComparison.Ordinal);
        Assert.EndsWith("$orderby=accountid%20asc", plan.FirstPage.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("odata.maxpagesize=5000", plan.Prefer);
    }

    [Fact]
    public void Named_columns_keep_their_order_and_the_watermark_column_is_always_read()
    {
        var table = Table(Attribute("name", "StringType", 100), Attribute("revenue", "MoneyType"), Attribute("versionnumber", "BigIntType"));
        var spec = DataverseTesting.Bounded(DataverseTesting.Spec(("columns", "revenue,name")), "versionnumber", "100", WatermarkKind.Whole);

        var plan = DataverseReadPlan.Build(table, DataverseSourceOptions.Parse(spec));

        Assert.Equal(["accountid", "revenue", "name", "versionnumber"], plan.Columns.Select(c => c.Name));
        Assert.True(plan.Bounded);
        var query = Uri.UnescapeDataString(plan.FirstPage.Query);
        Assert.Contains("$filter=versionnumber gt 100", query, StringComparison.Ordinal);
        Assert.Contains("$orderby=versionnumber asc,accountid asc", query, StringComparison.Ordinal);
    }

    [Fact]
    public void The_declared_watermark_orders_even_a_first_run_that_has_no_bound()
    {
        var table = Table(Attribute("modifiedon", "DateTimeType", behaviour: "UserLocal"));
        var spec = DataverseTesting.Spec((WatermarkPredicate.OrderColumnOption, "modifiedon"));

        var plan = DataverseReadPlan.Build(table, DataverseSourceOptions.Parse(spec));

        Assert.False(plan.Bounded);
        var query = Uri.UnescapeDataString(plan.FirstPage.Query);
        Assert.DoesNotContain("$filter", query, StringComparison.Ordinal);
        Assert.Contains("$orderby=modifiedon asc,accountid asc", query, StringComparison.Ordinal);
    }

    [Fact]
    public void An_authored_filter_is_combined_with_the_bound()
    {
        var table = Table(Attribute("statecode", "StateType"), Attribute("versionnumber", "BigIntType"));
        var spec = DataverseTesting.Bounded(DataverseTesting.Spec(("filter", "statecode eq 0")), "versionnumber", "7", WatermarkKind.Whole);

        var query = Uri.UnescapeDataString(DataverseReadPlan.Build(table, DataverseSourceOptions.Parse(spec)).FirstPage.Query);

        Assert.Contains("$filter=(statecode eq 0) and (versionnumber gt 7)", query, StringComparison.Ordinal);
    }

    [Fact]
    public void Excluding_an_unknown_column_or_a_column_every_read_needs_is_refused()
    {
        var table = Table(Attribute("name", "StringType", 100));

        Assert.Contains("does not have", Assert.Throws<SqlFlowException>(() => DataverseReadPlan.Build(table, Options(("excludeColumns", "nmae")))).Message, StringComparison.Ordinal);
        Assert.Contains("every read of the table needs", Assert.Throws<SqlFlowException>(() => DataverseReadPlan.Build(table, Options(("excludeColumns", "accountid")))).Message, StringComparison.Ordinal);
        var missing = Assert.Throws<SqlFlowException>(() => DataverseReadPlan.Build(table, Options(("columns", "revenue")))).Message;
        Assert.Contains("'columns' names 'revenue'", missing, StringComparison.Ordinal);
        Assert.Contains("accountid, name", missing, StringComparison.Ordinal);
    }

    [Fact]
    public void Labels_and_polymorphic_lookup_tables_land_beside_their_columns()
    {
        var table = Table(
            Attribute("statecode", "StateType"),
            Attribute("ownerid", "OwnerType"),
            Attribute("parentaccountid", "LookupType") with { Targets = ["account"] },
            Attribute("regardingobjectid", "LookupType") with { Targets = ["account", "contact"] });

        var plain = DataverseReadPlan.Build(table, Options());
        Assert.Equal(
            ["accountid", "ownerid", "owneridtype", "parentaccountid", "regardingobjectid", "regardingobjectidtype", "statecode"],
            plain.Columns.Select(c => c.Name));
        Assert.Equal("_ownerid_value@Microsoft.Dynamics.CRM.lookuplogicalname", plain.Columns.Single(c => c.Name == "owneridtype").Property);
        Assert.Contains("odata.include-annotations=\"*\"", plain.Prefer, StringComparison.Ordinal);

        var labelled = DataverseReadPlan.Build(table, Options(("formattedValues", "true")));
        Assert.Contains(labelled.Columns, c => c.Name == "statecodename" && c.Property == "statecode@OData.Community.Display.V1.FormattedValue");
        Assert.Contains(labelled.Columns, c => c.Name == "parentaccountidname" && c.Property == "_parentaccountid_value@OData.Community.Display.V1.FormattedValue");
        Assert.DoesNotContain("statecodename", Uri.UnescapeDataString(labelled.FirstPage.Query), StringComparison.Ordinal);
    }

    [Fact]
    public void A_label_that_would_land_on_a_column_of_the_table_is_refused()
    {
        var table = Table(Attribute("cr_status", "PicklistType"), Attribute("cr_statusname", "StringType", 100));

        var ex = Assert.Throws<SqlFlowException>(() => DataverseReadPlan.Build(table, Options(("formattedValues", "true"))));
        Assert.Contains("'cr_statusname'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("formattedValues", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_watermark_column_with_no_order_is_refused()
    {
        var table = Table(Attribute("parentaccountid", "LookupType"));
        var spec = DataverseTesting.Spec((WatermarkPredicate.OrderColumnOption, "parentaccountid"));

        var ex = Assert.Throws<SqlFlowException>(() => DataverseReadPlan.Build(table, DataverseSourceOptions.Parse(spec)));
        Assert.Contains("versionnumber", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(WatermarkKind.Whole, "638183964640916096", "BigIntType", null, "638183964640916096")]
    [InlineData(WatermarkKind.Fixed, "12.5000", "DecimalType", null, "12.5000")]
    [InlineData(WatermarkKind.Floating, "0.25", "DoubleType", null, "0.25")]
    [InlineData(WatermarkKind.DateTime, "2026-11-01T08:15:30.7000000", "DateTimeType", "UserLocal", "2026-11-01T08:15:30Z")]
    [InlineData(WatermarkKind.DateTimeOffset, "2026-11-01T10:15:30.0000000+02:00", "DateTimeType", "UserLocal", "2026-11-01T08:15:30Z")]
    [InlineData(WatermarkKind.DateTime, "2026-11-01T00:00:00.0000000", "DateTimeType", "DateOnly", "2026-11-01")]
    [InlineData(WatermarkKind.Text, "O'Bryan", "StringType", null, "'O''Bryan'")]
    public void The_bound_is_written_as_an_odata_literal(WatermarkKind kind, string canonical, string typeName, string? behaviour, string literal)
    {
        var column = DataverseReadPlan.ColumnOf(Attribute("c", typeName, 100, behaviour))!;

        Assert.Equal(literal, DataverseReadPlan.Literal(new DataverseWatermarkBound("c", canonical, kind), column));
    }

    [Fact]
    public void A_bound_whose_kind_no_longer_matches_the_column_is_refused_with_the_type_to_give_the_target()
    {
        var column = DataverseReadPlan.ColumnOf(Attribute("versionnumber", "BigIntType"))!;

        var ex = Assert.Throws<SqlFlowException>(() => DataverseReadPlan.Literal(new DataverseWatermarkBound("versionnumber", "abc", WatermarkKind.Text), column));
        Assert.Contains("BIGINT", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_table_too_wide_for_a_column_list_is_read_whole_rather_than_refused()
    {
        var wide = Enumerable.Range(0, 1500).Select(i => Attribute($"cr6b2_a_rather_long_column_name_{i:D4}", "IntegerType")).ToArray();

        var plan = DataverseReadPlan.Build(Table(wide), Options());

        Assert.False(plan.SelectsColumns);
        Assert.DoesNotContain("$select", plan.FirstPage.AbsoluteUri, StringComparison.Ordinal);
        Assert.True(plan.FirstPage.AbsoluteUri.Length <= DataverseReadPlan.MaxUrlLength);
        Assert.Equal(1501, plan.Columns.Count);
    }
}
