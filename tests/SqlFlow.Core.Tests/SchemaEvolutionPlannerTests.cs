using SqlFlow.SqlServer.Schema;
using Xunit;

namespace SqlFlow.Tests;

public sealed class SchemaEvolutionPlannerTests
{
    private static SqlColumn Col(string name, string type, bool nullable = true, ColumnRole role = ColumnRole.Source)
        => new() { Name = name, DataType = SqlDataType.Parse(type), IsNullable = nullable, Role = role };

    private static readonly IReadOnlySet<string> NoKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void NoTarget_CreatesTable()
    {
        var desired = new[] { Col("Id", "int") };
        var plan = SchemaEvolutionPlanner.Plan(desired, actual: null, NoKeys);
        Assert.True(plan.CreateTable);
        Assert.Equal(desired, plan.CreateColumns);
        Assert.False(plan.IsBlocked);
    }

    [Fact]
    public void NewColumn_IsAdded()
    {
        var plan = SchemaEvolutionPlanner.Plan(
            [Col("Id", "int"), Col("Name", "nvarchar(50)")],
            [Col("Id", "int")],
            NoKeys);

        var added = Assert.Single(plan.ColumnsToAdd);
        Assert.Equal("Name", added.Name);
        Assert.Empty(plan.ColumnsToAlter);
    }

    [Fact]
    public void SameType_NoChange()
        => Assert.False(SchemaEvolutionPlanner.Plan([Col("Id", "int")], [Col("Id", "int")], NoKeys).HasChanges);

    [Fact]
    public void TargetAlreadyWider_NoChange()
        => Assert.False(SchemaEvolutionPlanner.Plan([Col("Name", "nvarchar(50)")], [Col("Name", "nvarchar(100)")], NoKeys).HasChanges);

    [Fact]
    public void VariableWiden_IsMetadataOnlyAlter()
    {
        var plan = SchemaEvolutionPlanner.Plan([Col("Name", "nvarchar(100)")], [Col("Name", "nvarchar(50)")], NoKeys);
        var alter = Assert.Single(plan.ColumnsToAlter);
        Assert.Equal("nvarchar(100)", alter.ToType.Render());
        Assert.Equal(ChangeFootprint.MetadataOnly, alter.Footprint);
        Assert.False(plan.HasRewrite);
    }

    [Fact]
    public void IntegerWiden_IsTableRewriteAlter()
    {
        var plan = SchemaEvolutionPlanner.Plan([Col("Amount", "bigint")], [Col("Amount", "int")], NoKeys);
        var alter = Assert.Single(plan.ColumnsToAlter);
        Assert.Equal(ChangeFootprint.TableRewrite, alter.Footprint);
        Assert.True(plan.HasRewrite);
        Assert.Single(plan.RewriteColumns);
    }

    [Fact]
    public void IncompatibleOnOrdinaryColumn_IsDrift_NotBlocked()
    {
        var plan = SchemaEvolutionPlanner.Plan([Col("X", "int")], [Col("X", "nvarchar(50)")], NoKeys);
        Assert.False(plan.IsBlocked);
        Assert.Contains(plan.Drift, d => d.Kind == DriftKind.IncompatibleOrdinaryColumn && d.Column == "X");
    }

    [Fact]
    public void IncompatibleOnKeyColumn_IsCritical_Blocks()
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "K" };
        var plan = SchemaEvolutionPlanner.Plan([Col("K", "int")], [Col("K", "nvarchar(50)")], keys);
        Assert.True(plan.IsBlocked);
        Assert.Contains(plan.CriticalMismatches, m => m.Column == "K");
    }

    [Fact]
    public void IncompatibleOnHashColumn_IsCritical()
    {
        var plan = SchemaEvolutionPlanner.Plan(
            [Col("HashKey_DW", "binary(32)", role: ColumnRole.HashKey)],
            [Col("HashKey_DW", "int")],
            NoKeys);
        Assert.True(plan.IsBlocked);
    }

    [Fact]
    public void ExtraTargetColumn_IsDrift_NeverDropped()
    {
        var plan = SchemaEvolutionPlanner.Plan(
            [Col("Id", "int")],
            [Col("Id", "int"), Col("Legacy", "nvarchar(50)")],
            NoKeys);

        Assert.Contains(plan.Drift, d => d.Kind == DriftKind.ExtraTargetColumn && d.Column == "Legacy");
        Assert.False(plan.HasChanges);
    }

    [Fact]
    public void NullabilityTightening_IsDrift_NotApplied()
    {
        var plan = SchemaEvolutionPlanner.Plan(
            [Col("Id", "int", nullable: false)],
            [Col("Id", "int", nullable: true)],
            NoKeys);

        Assert.Contains(plan.Drift, d => d.Kind == DriftKind.NullabilityNotTightened && d.Column == "Id");
    }
}
