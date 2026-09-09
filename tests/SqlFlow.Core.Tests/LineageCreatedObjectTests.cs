using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Extraction;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The created-object capture specification: for every CREATE TABLE/VIEW/PROCEDURE/FUNCTION/TRIGGER (and
/// CTAS / SELECT INTO) the extractor records the verbatim generating DDL, and for a table its column
/// definitions or for a view/inline-TVF/CTAS its interpreted projection columns (the SELECT list's aliases
/// and cast targets), so the catalog can attach an object's script and an offline column dictionary without a
/// live connection. Temp objects are script-local and never captured.
/// </summary>
public sealed class LineageCreatedObjectTests
{
    private static ScriptDependencies Extract(string sql, string? defaultDatabase = null)
        => TSqlLineageExtractor.Extract(sql, "test", defaultDatabase);

    [Fact]
    public void CreateTable_CapturesDdlAndColumns()
    {
        var deps = Extract("""
            CREATE TABLE DW.dbo.Customer (
                Id INT NOT NULL,
                Name NVARCHAR(100) NULL,
                Balance DECIMAL(18,2) NOT NULL
            );
            """);

        var created = Assert.Single(deps.CreatedObjects.Values);
        Assert.Equal("dw.dbo.customer", created.Table.Key);
        Assert.Equal(LineageNodeKind.Table, created.Kind);
        Assert.Contains("CREATE TABLE", created.Ddl, StringComparison.Ordinal);

        Assert.Collection(created.Columns,
            c => { Assert.Equal("Id", c.Name); Assert.Equal("INT", c.DataType); Assert.False(c.Nullable); },
            c => { Assert.Equal("Name", c.Name); Assert.Equal("NVARCHAR(100)", c.DataType); Assert.True(c.Nullable); },
            c => { Assert.Equal("Balance", c.Name); Assert.Equal("DECIMAL(18,2)", c.DataType); Assert.False(c.Nullable); });
    }

    [Fact]
    public void CreateView_CapturesDdl_AndProjectionColumnNames()
    {
        var deps = Extract("CREATE VIEW dbo.vX AS SELECT c.Id, c.Name AS FullName FROM DW.dbo.Customers c;");

        var created = Assert.Single(deps.CreatedObjects.Values);
        Assert.Equal(LineageNodeKind.View, created.Kind);
        Assert.Contains("CREATE VIEW", created.Ddl, StringComparison.Ordinal);

        // A bare column reference takes its trailing identifier; an aliased one takes the alias. Neither
        // carries a type (only a cast/convert does), so the interpreted type is null.
        Assert.Collection(created.Columns,
            c => { Assert.Equal("Id", c.Name); Assert.Null(c.DataType); },
            c => { Assert.Equal("FullName", c.Name); Assert.Null(c.DataType); });
    }

    [Fact]
    public void CreateOrAlterView_WithCastProjection_CapturesColumnNamesAndTypes()
    {
        // The engine's generated transform view: every column is CAST(...) AS [Alias], so both the name and
        // the target type are interpreted from the projection.
        var deps = Extract("""
            CREATE OR ALTER VIEW [pre].[v_Order]
            AS
            SELECT
                CAST([MEDIA_TYPE] AS int) AS [MEDIA_TYPE],
                CAST([SESSION_NR] AS DECIMAL(12,0)) AS [SESSION_NR]
            FROM [raw].[Order];
            """);

        var created = Assert.Single(deps.CreatedObjects.Values);
        Assert.Equal(LineageNodeKind.View, created.Kind);
        Assert.Collection(created.Columns,
            c => { Assert.Equal("MEDIA_TYPE", c.Name); Assert.Equal("int", c.DataType); Assert.True(c.Nullable); },
            c => { Assert.Equal("SESSION_NR", c.Name); Assert.Equal("DECIMAL(12,0)", c.DataType); Assert.True(c.Nullable); });
    }

    [Fact]
    public void SelectStarView_CapturesDdl_ButNoColumns()
    {
        // A star projection cannot be enumerated without the source schema: declared, not guessed.
        var deps = Extract("CREATE VIEW dbo.vAll AS SELECT * FROM DW.dbo.Customers;");

        var created = Assert.Single(deps.CreatedObjects.Values);
        Assert.Equal(LineageNodeKind.View, created.Kind);
        Assert.Empty(created.Columns);
    }

    [Fact]
    public void CreateProcedure_CapturesDdl()
    {
        var deps = Extract("CREATE PROCEDURE dbo.LoadCustomers AS BEGIN INSERT INTO DW.dbo.Customer SELECT * FROM DW.dbo.Staging; END;");

        var created = deps.CreatedObjects.Values.Single(o => o.Kind == LineageNodeKind.Procedure);
        Assert.Equal("dbo.loadcustomers", created.Table.Key);
        Assert.Contains("CREATE PROCEDURE", created.Ddl, StringComparison.Ordinal);
        Assert.Empty(created.Columns);
    }

    [Fact]
    public void InlineTableValuedFunction_CapturesDdl_AndProjectionColumns()
    {
        var deps = Extract("CREATE FUNCTION dbo.ActiveCustomers() RETURNS TABLE AS RETURN (SELECT c.Id, c.Name FROM DW.dbo.Customers c WHERE c.Active = 1);");

        var created = deps.CreatedObjects.Values.Single(o => o.Kind == LineageNodeKind.Function);
        Assert.Equal("dbo.activecustomers", created.Table.Key);
        Assert.Contains("CREATE FUNCTION", created.Ddl, StringComparison.Ordinal);
        Assert.Collection(created.Columns,
            c => Assert.Equal("Id", c.Name),
            c => Assert.Equal("Name", c.Name));
    }

    [Fact]
    public void CreateTrigger_CapturesDdl()
    {
        var deps = Extract("CREATE TRIGGER dbo.trgAudit ON DW.dbo.Customer AFTER INSERT AS BEGIN INSERT INTO DW.dbo.Audit SELECT * FROM inserted; END;");

        var created = deps.CreatedObjects.Values.Single(o => o.Kind == LineageNodeKind.Trigger);
        Assert.Equal("dbo.trgaudit", created.Table.Key);
        Assert.Contains("CREATE TRIGGER", created.Ddl, StringComparison.Ordinal);
        Assert.Empty(created.Columns);
    }

    [Fact]
    public void SelectInto_CapturesTheCreatedTable()
    {
        var deps = Extract("SELECT c.Id INTO DW.dbo.Snapshot FROM DW.dbo.Customers c;");

        var created = Assert.Single(deps.CreatedObjects.Values);
        Assert.Equal("dw.dbo.snapshot", created.Table.Key);
        Assert.Equal(LineageNodeKind.Table, created.Kind);
    }

    [Fact]
    public void TempTable_IsNotCaptured()
    {
        var deps = Extract("CREATE TABLE #Staging (Id INT NOT NULL);");

        Assert.Empty(deps.CreatedObjects);
    }
}
