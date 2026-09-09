using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Extraction;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The interpreted data model's extraction rules: equality joins (aliases resolved, composites folded,
/// CTEs/derived tables excluded), MERGE match keys, and explicit PRIMARY KEY / FOREIGN KEY clauses parsed
/// from DDL. This is the AST-side foundation of the catalog's PK/FK view: warehouses rarely declare physical
/// constraints, so the model is read from how the codebase's SQL actually joins and loads.
/// </summary>
public sealed class LineageDataModelTests
{
    private static ScriptDependencies Extract(string sql, string? defaultDatabase = null)
        => TSqlLineageExtractor.Extract(sql, "test", defaultDatabase);

    // ---- Join inference --------------------------------------------------------------------------------

    [Fact]
    public void JoinOn_ResolvesAliases_ToRealTables()
    {
        var deps = Extract("SELECT * FROM DW.dbo.Orders o JOIN DW.dbo.Customers c ON o.CustomerId = c.Id;");

        // The observation is canonicalised onto the ordinally-smaller table rather than kept in the order the
        // predicate happened to be written. That is what makes "o.CustomerId = c.Id" and "c.Id = o.CustomerId"
        // one relationship instead of two, and it is why a predicate tree mixing both directions does not
        // report the same join twice. The columns stay paired with their own side across the swap.
        var join = Assert.Single(deps.Joins);
        Assert.Equal("dw.dbo.customers", join.Left.Key);
        Assert.Equal(["Id"], join.LeftColumns);
        Assert.Equal("dw.dbo.orders", join.Right.Key);
        Assert.Equal(["CustomerId"], join.RightColumns);
    }

    [Fact]
    public void JoinOn_FoldsBothWritingDirections_IntoOneObservation()
    {
        // The same relationship written each way round in two scripts, and once with the sides mixed inside a
        // single ON clause. Every form must land on the same canonical observation.
        var written = Extract("SELECT * FROM DW.dbo.Orders o JOIN DW.dbo.Customers c ON o.CustomerId = c.Id;");
        var reversed = Extract("SELECT * FROM DW.dbo.Customers c JOIN DW.dbo.Orders o ON c.Id = o.CustomerId;");

        var a = Assert.Single(written.Joins);
        var b = Assert.Single(reversed.Joins);
        Assert.Equal(a.Left.Key, b.Left.Key);
        Assert.Equal(a.LeftColumns, b.LeftColumns);
        Assert.Equal(a.Right.Key, b.Right.Key);
        Assert.Equal(a.RightColumns, b.RightColumns);
    }

    [Fact]
    public void CompositeJoin_FoldsIntoOneObservation_InPredicateOrder()
    {
        var deps = Extract("""
            SELECT * FROM DW.dbo.TransItem ti
            JOIN DW.dbo.Trans t ON ti.StoreId = t.StoreId AND ti.TransNo = t.TransNo;
            """);

        var join = Assert.Single(deps.Joins);
        Assert.Equal(["StoreId", "TransNo"], join.LeftColumns);
        Assert.Equal(["StoreId", "TransNo"], join.RightColumns);
    }

    [Fact]
    public void WhereClauseEquiJoin_IsObserved()
    {
        var deps = Extract("""
            SELECT * FROM DW.dbo.Orders o, DW.dbo.Customers c WHERE o.CustomerId = c.Id;
            """);

        // Canonical orientation, as everywhere else: the pair, not the writing order, is the identity.
        var join = Assert.Single(deps.Joins);
        Assert.Equal("dw.dbo.customers", join.Left.Key);
        Assert.Equal("dw.dbo.orders", join.Right.Key);
        Assert.Equal(JoinTypes.Where, join.JoinType);
    }

    [Fact]
    public void RepeatedJoin_AndReversedSpelling_CountOnce()
    {
        var deps = Extract("""
            SELECT * FROM DW.dbo.Orders o JOIN DW.dbo.Customers c ON o.CustomerId = c.Id;
            SELECT * FROM DW.dbo.Customers c JOIN DW.dbo.Orders o ON c.Id = o.CustomerId;
            """);

        Assert.Single(deps.Joins);
    }

    [Fact]
    public void JoinAgainstCteOrDerivedTable_ObservesNothing()
    {
        var deps = Extract("""
            WITH Recent AS (SELECT * FROM DW.dbo.Orders)
            SELECT * FROM Recent r JOIN DW.dbo.Customers c ON r.CustomerId = c.Id;
            SELECT * FROM (SELECT * FROM DW.dbo.Orders) d JOIN DW.dbo.Customers c ON d.CustomerId = c.Id;
            """);

        Assert.Empty(deps.Joins);
    }

    [Fact]
    public void OrBranches_AndLiteralComparisons_AreFiltersNotJoins()
    {
        var deps = Extract("""
            SELECT * FROM DW.dbo.Orders o JOIN DW.dbo.Customers c
              ON (o.CustomerId = c.Id OR o.LegacyCustomerId = c.Id)
            WHERE o.Status = 'open' AND c.Id = 42;
            """);

        Assert.Empty(deps.Joins);
    }

    [Fact]
    public void UpdateFromJoin_IsObserved()
    {
        var deps = Extract("""
            UPDATE t SET t.Amount = s.Amount
            FROM DW.dbo.Target t JOIN DW.dbo.Source s ON t.BusinessKey = s.BusinessKey;
            """);

        var join = Assert.Single(deps.Joins);
        Assert.Equal(["BusinessKey"], join.LeftColumns);
    }

    [Fact]
    public void SelfJoin_IsNotARelationship()
    {
        var deps = Extract("SELECT * FROM DW.dbo.Emp a JOIN DW.dbo.Emp b ON a.ManagerId = b.Id;");

        Assert.Empty(deps.Joins);
    }

    // ---- Key inference ---------------------------------------------------------------------------------

    [Fact]
    public void MergeOn_YieldsTargetMatchKey()
    {
        var deps = Extract("""
            MERGE DW.dbo.Trans AS t
            USING DW.stg.Trans AS s ON t.StoreId = s.StoreId AND t.TransNo = s.TransNo
            WHEN MATCHED THEN UPDATE SET t.Amount = s.Amount
            WHEN NOT MATCHED THEN INSERT (StoreId, TransNo, Amount) VALUES (s.StoreId, s.TransNo, s.Amount);
            """);

        var key = Assert.Single(deps.Keys);
        Assert.Equal("dw.dbo.trans", key.Table.Key);
        Assert.Equal(LineageModelOrigin.Merge, key.Origin);
        Assert.Equal(["StoreId", "TransNo"], key.Columns);
    }

    [Fact]
    public void CreateTable_PrimaryKeyAndForeignKey_AreParsed()
    {
        var deps = Extract("""
            CREATE TABLE DW.dbo.OrderLine (
                OrderId int NOT NULL,
                LineNumber int NOT NULL,
                ProductId int NOT NULL CONSTRAINT FK_OrderLine_Product FOREIGN KEY REFERENCES DW.dbo.Product (Id),
                CONSTRAINT PK_OrderLine PRIMARY KEY (OrderId, LineNumber),
                CONSTRAINT FK_OrderLine_Order FOREIGN KEY (OrderId) REFERENCES DW.dbo.Orders (Id)
            );
            """);

        var key = Assert.Single(deps.Keys);
        Assert.Equal(LineageModelOrigin.Constraint, key.Origin);
        Assert.Equal(["OrderId", "LineNumber"], key.Columns);

        Assert.Equal(2, deps.ForeignKeys.Count);
        var columnLevel = deps.ForeignKeys.Single(fk => fk.Name == "FK_OrderLine_Product");
        Assert.Equal(["ProductId"], columnLevel.FromColumns);
        Assert.Equal("dw.dbo.product", columnLevel.To.Key);
        Assert.Equal(["Id"], columnLevel.ToColumns);
        var tableLevel = deps.ForeignKeys.Single(fk => fk.Name == "FK_OrderLine_Order");
        Assert.Equal(["OrderId"], tableLevel.FromColumns);
        Assert.Equal("dw.dbo.orders", tableLevel.To.Key);
    }

    [Fact]
    public void AlterTableAddConstraint_IsParsed()
    {
        var deps = Extract("""
            ALTER TABLE DW.dbo.OrderLine ADD CONSTRAINT FK_Late FOREIGN KEY (OrderId) REFERENCES DW.dbo.Orders (Id);
            """);

        var constraint = Assert.Single(deps.ForeignKeys);
        Assert.Equal("FK_Late", constraint.Name);
        Assert.Equal("dw.dbo.orderline", constraint.From.Key);
        Assert.Equal("dw.dbo.orders", constraint.To.Key);
    }

    [Fact]
    public void TempTableConstraints_AreObservedButExcludableByTempFlag()
    {
        var deps = Extract("CREATE TABLE #stage (Id int NOT NULL PRIMARY KEY);");

        // The walker records the observation; the collector-level hygiene (ScriptFactBuilder) drops temp
        // names, so the flag is what the filter keys on.
        var key = Assert.Single(deps.Keys);
        Assert.True(key.Table.IsTemp);
    }
}
