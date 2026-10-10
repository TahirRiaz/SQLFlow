using SqlFlow.Lineage.Collection;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The model half of a pbix-extract specification is read back, not discarded: tables with their Power Query source,
/// the columns, calculated columns, and measures defined on each, and the relationships between tables. Tested against
/// the checked-in sample specification (the tool's real output for the AdventureWorks sample report), so the contract
/// with the C tool is exercised without needing the extractor binary on this machine.
/// </summary>
public sealed class PbixModelSpecParsingTests
{
    private static PbixModel SampleModel()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "samples", "powerbi", "AdventureWorks_Sales.spec.yaml");
        Assert.True(File.Exists(path), $"Expected the sample specification at {Path.GetFullPath(path)}.");

        return PbixExtractTool.Parse(File.ReadAllText(path), "pbix-extract").Model;
    }

    [Fact]
    public void ATable_CarriesItsPowerQuerySource_AndTheWarehouseObjectItResolvedTo()
    {
        var customer = Assert.Single(SampleModel().Tables, t => t.Name == "Customer");

        Assert.Contains("Sql.Database", customer.PowerQuery, StringComparison.Ordinal);
        Assert.Equal("AdventureWorks", customer.SourceDatabase);
        Assert.Equal("dbo", customer.SourceSchema);
        Assert.Equal("DimCustomer", customer.SourceName);
    }

    [Fact]
    public void AColumn_IsNamedWithinItsTable_WithItsDataType()
    {
        var customer = Assert.Single(SampleModel().Tables, t => t.Name == "Customer");

        // A column name with a space, read off the table's hasColumn edge rather than split from the node id.
        var column = Assert.Single(customer.Fields, f => f.Name == "Customer ID");
        Assert.Equal("column", column.Kind);
        Assert.Equal("string", column.DataType);
        Assert.Equal("int64", Assert.Single(customer.Fields, f => f.Name == "CustomerKey").DataType);
    }

    [Fact]
    public void AMeasure_IsDefinedOnItsTable_WithItsDax()
    {
        var sales = Assert.Single(SampleModel().Tables, t => t.Name == "Sales");

        var measure = Assert.Single(sales.Fields, f => f.Name == "Sales Amount by Due Date");
        Assert.Equal("measure", measure.Kind);
        Assert.Contains("USERELATIONSHIP(Sales[DueDateKey],'Date'[DateKey])", measure.Expression, StringComparison.Ordinal);

        // Plain columns are listed before the table's measures.
        Assert.Equal("measure", sales.Fields[^1].Kind);
    }

    [Fact]
    public void Relationships_CarryTheirColumnsCardinality_AndWhetherTheyAreActive()
    {
        var relationships = SampleModel().Relationships;

        var dueDate = Assert.Single(relationships, r => r.FromTable == "Sales" && r.FromColumn == "DueDateKey");
        Assert.Equal("Date", dueDate.ToTable);
        Assert.Equal("DateKey", dueDate.ToColumn);
        Assert.Equal("M:1", dueDate.Cardinality);
        Assert.False(dueDate.IsActive);

        var category = Assert.Single(relationships, r => r.FromTable == "Product" && r.ToTable == "Table");
        Assert.Equal("Category", category.FromColumn);
        Assert.True(category.IsActive);
    }
}
