using SqlFlow.Core;
using SqlFlow.Core.Engine;
using SqlFlow.Core.Model;
using SqlFlow.SqlServer.Schema;
using Xunit;

namespace SqlFlow.Tests;

public sealed class SchemaDifferTests
{
    private static readonly SqlServerColumnTypeReconciler Reconciler = new();

    private static TableSchema Desired(params string[] names) => new()
    {
        Schema = "dbo",
        Table = "T",
        Columns = names.Select(n => new ColumnDefinition { Name = n, SqlType = "INT" }).ToList(),
    };

    private static TableSchema Existing(params string[] names) => new()
    {
        Schema = "dbo",
        Table = "T",
        Columns = names.Select(n => new ColumnDefinition { Name = n, SqlType = "INT" }).ToList(),
    };

    private static TableSchema Table(params (string Name, string Type)[] columns) => new()
    {
        Schema = "dbo",
        Table = "T",
        Columns = columns.Select(c => new ColumnDefinition { Name = c.Name, SqlType = c.Type }).ToList(),
    };

    [Fact]
    public void Diff_NoTable_CreatesTable()
    {
        var delta = SchemaDiffer.Diff(Desired("A", "B"), actual: null, SchemaEvolution.Widen, Reconciler);

        Assert.True(delta.CreateTable);
        Assert.Equal(2, delta.ColumnsToAdd.Count);
        Assert.Empty(delta.ColumnsToAlter);
    }

    [Fact]
    public void Diff_Widen_AddsOnlyMissingColumns()
    {
        var delta = SchemaDiffer.Diff(Desired("A", "B"), Existing("A"), SchemaEvolution.Widen, Reconciler);

        Assert.False(delta.CreateTable);
        Assert.Single(delta.ColumnsToAdd);
        Assert.Equal("B", delta.ColumnsToAdd[0].Name);
    }

    [Fact]
    public void Diff_Widen_WidensNarrowExistingColumn()
    {
        var desired = Table(("Remarks", "varchar(4000)"));
        var actual = Table(("Remarks", "VARCHAR(255)"));

        var delta = SchemaDiffer.Diff(desired, actual, SchemaEvolution.Widen, Reconciler);

        Assert.False(delta.CreateTable);
        Assert.Empty(delta.ColumnsToAdd);
        var alter = Assert.Single(delta.ColumnsToAlter);
        Assert.Equal("Remarks", alter.Name);
        Assert.Equal("varchar(4000)", alter.SqlType);
    }

    [Fact]
    public void Diff_Widen_AddsAndWidensTogether()
    {
        var desired = Table(("Remarks", "varchar(4000)"), ("Notes", "varchar(4000)"));
        var actual = Table(("Remarks", "VARCHAR(255)"));

        var delta = SchemaDiffer.Diff(desired, actual, SchemaEvolution.Widen, Reconciler);

        Assert.Equal("Notes", Assert.Single(delta.ColumnsToAdd).Name);
        Assert.Equal("Remarks", Assert.Single(delta.ColumnsToAlter).Name);
    }

    [Fact]
    public void Diff_Widen_KeepsColumnAlreadyWideEnough()
    {
        var desired = Table(("Remarks", "varchar(255)"));
        var actual = Table(("Remarks", "VARCHAR(4000)"));

        var delta = SchemaDiffer.Diff(desired, actual, SchemaEvolution.Widen, Reconciler);

        Assert.False(delta.HasChanges);
    }

    [Fact]
    public void Diff_Widen_PreservesExistingNullabilityOnWiden()
    {
        var desired = Table(("Remarks", "varchar(4000)"));
        var actual = new TableSchema
        {
            Schema = "dbo",
            Table = "T",
            Columns = [new ColumnDefinition { Name = "Remarks", SqlType = "VARCHAR(255)", IsNullable = false }],
        };

        var delta = SchemaDiffer.Diff(desired, actual, SchemaEvolution.Widen, Reconciler);

        Assert.False(Assert.Single(delta.ColumnsToAlter).IsNullable);
    }

    [Fact]
    public void Diff_Widen_LeavesIncompatibleFamilyAlone()
    {
        var desired = Table(("Value", "varchar(4000)"));
        var actual = Table(("Value", "INT"));

        var delta = SchemaDiffer.Diff(desired, actual, SchemaEvolution.Widen, Reconciler);

        Assert.False(delta.HasChanges);
    }

    [Fact]
    public void Diff_Create_DoesNotWidenExistingColumn()
    {
        var desired = Table(("Remarks", "varchar(4000)"));
        var actual = Table(("Remarks", "VARCHAR(255)"));

        var delta = SchemaDiffer.Diff(desired, actual, SchemaEvolution.Create, Reconciler);

        Assert.False(delta.HasChanges);
    }

    [Fact]
    public void Diff_Strict_ThrowsOnDrift()
        => Assert.Throws<SchemaDriftException>(
            () => SchemaDiffer.Diff(Desired("A", "B"), Existing("A"), SchemaEvolution.Strict, Reconciler));

    [Fact]
    public void Diff_Create_DoesNotAlterExistingTable()
    {
        var delta = SchemaDiffer.Diff(Desired("A", "B"), Existing("A"), SchemaEvolution.Create, Reconciler);

        Assert.False(delta.HasChanges);
    }
}
