using SqlFlow.Lineage.Extraction;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// What the extractor records as a JOIN, which is the raw material of the interpreted data model and therefore
/// of every query composed against it. Two properties are pinned here, and they pull in opposite directions:
/// the model must capture the joins a warehouse really uses (including the temporal interval lookups that are
/// not equalities at all), and it must NOT capture predicates that merely mention two tables, because a
/// fabricated relationship is worse than a missing one.
/// </summary>
public sealed class LineageJoinObservationTests
{
    private static IReadOnlyList<ObservedJoin> Joins(string sql)
        => TSqlLineageExtractor.Extract(sql, "test", null).Joins;

    // ---- Equi-joins, and the join type that comes with them ----------------------------------------

    [Fact]
    public void EquiJoin_RecordsThePairedColumns_AndThatItWasInner()
    {
        var join = Assert.Single(Joins(
            "SELECT * FROM DW.dbo.Orders o JOIN DW.dbo.Customers c ON c.Id = o.CustomerId;"));

        Assert.Equal(["Id"], join.LeftColumns);
        Assert.Equal(["CustomerId"], join.RightColumns);
        Assert.Equal(JoinTypes.Inner, join.JoinType);

        // An equi-join carries "=" per pair, which the model normalises away; what matters here is that it is
        // an equality and nothing else.
        Assert.All(join.Operators, o => Assert.Equal("=", o));
    }

    [Theory]
    [InlineData("LEFT JOIN", JoinTypes.Left)]
    [InlineData("LEFT OUTER JOIN", JoinTypes.Left)]
    [InlineData("RIGHT JOIN", JoinTypes.Right)]
    [InlineData("FULL OUTER JOIN", JoinTypes.Full)]
    [InlineData("INNER JOIN", JoinTypes.Inner)]
    public void JoinType_IsRecorded_BecauseInnerWhereTheEstateWritesLeftDropsRows(string syntax, string expected)
    {
        var join = Assert.Single(Joins(
            $"SELECT * FROM DW.dbo.Orders o {syntax} DW.dbo.Customers c ON c.Id = o.CustomerId;"));

        Assert.Equal(expected, join.JoinType);
    }

    [Fact]
    public void OldStyleCommaJoin_IsRecorded_AndSaysItCameFromAWhereClause()
    {
        var join = Assert.Single(Joins(
            "SELECT * FROM DW.dbo.Orders o, DW.dbo.Customers c WHERE c.Id = o.CustomerId;"));

        Assert.Equal(JoinTypes.Where, join.JoinType);
        Assert.Equal(["Id"], join.LeftColumns);
    }

    [Fact]
    public void CompositeKey_FoldsIntoOneObservation_NotOnePerColumn()
    {
        var join = Assert.Single(Joins("""
            SELECT * FROM DW.dbo.Sales s
            JOIN DW.dbo.Dim d ON d.RouteId = s.RouteId AND d.Year = s.Year;
            """));

        Assert.Equal(2, join.LeftColumns.Count);
        Assert.Equal(2, join.RightColumns.Count);
    }

    // ---- Temporal / range joins ---------------------------------------------------------------------

    [Fact]
    public void TemporalJoin_IsRecorded_WithItsRealOperators()
    {
        // The SCD2 dimension lookup: no equality anywhere, so the old collector saw nothing at all.
        var join = Assert.Single(Joins("""
            SELECT * FROM DW.dbo.Facts f
            JOIN DW.dbo.DimRate d ON f.Dato >= d.ValidFrom AND f.Dato < d.ValidTo;
            """));

        // The pair is canonicalised onto the ordinally-smaller table, which mirrors the operators, so the
        // property to assert is the INTERVAL: one lower bound and one upper bound, and no equality.
        Assert.Equal(2, join.Operators.Count);
        Assert.Contains(join.Operators, o => o is ">" or ">=");
        Assert.Contains(join.Operators, o => o is "<" or "<=");
        Assert.DoesNotContain("=", join.Operators);
    }

    [Fact]
    public void MixedJoin_PrefersTheEqualityAndDoesNotMixInTheRangeBound()
    {
        // A surrogate key plus a validity window. The equality IS the relationship; folding the range bounds
        // in beside it would describe a key match that does not exist.
        var join = Assert.Single(Joins("""
            SELECT * FROM DW.dbo.Facts f
            JOIN DW.dbo.DimRate d ON d.RouteId = f.RouteId AND f.Dato >= d.ValidFrom AND f.Dato < d.ValidTo;
            """));

        Assert.All(join.Operators, o => Assert.Equal("=", o));
        Assert.Equal(["RouteId"], join.LeftColumns);
    }

    [Fact]
    public void LoneInequality_IsNotAJoin_BecauseMostOfThemAreFilters()
    {
        // One unbounded comparison between two tables is a filter that happens to mention both. Recording it
        // would put a relationship in the model that nobody joins on.
        Assert.Empty(Joins("""
            SELECT * FROM DW.dbo.Orders o, DW.dbo.Limits l
            WHERE o.Amount > l.Threshold;
            """));
    }

    [Fact]
    public void UnboundedRange_NeedsBothEnds_BeforeItCountsAsAnInterval()
    {
        // Only a lower bound: still a filter, not an interval containment.
        Assert.Empty(Joins("""
            SELECT * FROM DW.dbo.Facts f
            JOIN DW.dbo.DimRate d ON f.Dato >= d.ValidFrom;
            """));
    }

    // ---- What must never become a relationship ------------------------------------------------------

    [Fact]
    public void NotEqual_IsNeverJoinIdentity()
    {
        Assert.Empty(Joins("""
            SELECT * FROM DW.dbo.Orders o
            JOIN DW.dbo.Customers c ON c.Id <> o.CustomerId;
            """));
    }

    [Fact]
    public void OrBranches_ContributeNothing_BecauseADisjunctionIsNotIdentity()
    {
        Assert.Empty(Joins("""
            SELECT * FROM DW.dbo.Orders o
            JOIN DW.dbo.Customers c ON c.Id = o.CustomerId OR c.AltId = o.CustomerId;
            """));
    }

    [Fact]
    public void UnqualifiedColumns_ResolveToNothing_RatherThanBeingGuessed()
    {
        Assert.Empty(Joins("SELECT * FROM DW.dbo.Orders o, DW.dbo.Customers c WHERE Id = CustomerId;"));
    }

    [Fact]
    public void ASelfJoin_IsNotARelationshipToItself()
    {
        Assert.Empty(Joins("""
            SELECT * FROM DW.dbo.Orders a
            JOIN DW.dbo.Orders b ON b.ParentId = a.Id;
            """));
    }

    [Fact]
    public void ComparingAColumnToALiteral_IsAFilter_NotAJoin()
    {
        Assert.Empty(Joins("SELECT * FROM DW.dbo.Orders o WHERE o.Status = 'open';"));
    }

    [Fact]
    public void MergeMatchKey_TakesOnlyTheEqualities_NotTheWindowNarrowingTheMatch()
    {
        // A MERGE match key is an equality match by definition; a range predicate in the ON clause narrows
        // which rows are considered and is never part of the key.
        var deps = TSqlLineageExtractor.Extract("""
            MERGE DW.dbo.Target AS t
            USING DW.dbo.Stage AS s
              ON t.BusinessKey = s.BusinessKey AND s.LoadedAt >= t.ValidFrom AND s.LoadedAt < t.ValidTo
            WHEN MATCHED THEN UPDATE SET t.Amount = s.Amount;
            """, "test", null);

        var key = Assert.Single(deps.Keys);
        Assert.Equal(["BusinessKey"], key.Columns);
    }
}
