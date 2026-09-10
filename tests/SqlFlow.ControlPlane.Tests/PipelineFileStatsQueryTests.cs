using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The file-size profile queries must translate to SQL in full: the profile aggregates a pipeline's whole file
/// history, so a shape that falls back to client evaluation would drag every recorded file into memory. Translation
/// happens when the query is compiled, not when it runs, so these assertions need no database.
/// </summary>
public sealed class PipelineFileStatsQueryTests
{
    // A syntactically valid connection string that is never opened: ToQueryString compiles the query against the
    // SQL Server provider without contacting a server.
    private const string OfflineCatalog = "Server=(local);Database=SqlFlowQueryShape;Trusted_Connection=True;";

    [Fact]
    public void DistinctFiles_DeduplicatesByNameAndPath_InSql()
    {
        using var db = CatalogDatabase.Create(OfflineCatalog);

        var sql = PipelineFileStats.DistinctFiles(db, Guid.NewGuid()).ToQueryString();

        Assert.Contains("GROUP BY", sql, StringComparison.Ordinal);
        Assert.Contains("[Name]", sql, StringComparison.Ordinal);
        Assert.Contains("[Path]", sql, StringComparison.Ordinal);
        Assert.Contains("MAX(", sql, StringComparison.Ordinal);
        Assert.Contains("[SizeBytes]", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Aggregate_ComputesEverySumAndExtreme_InOneStatement()
    {
        using var db = CatalogDatabase.Create(OfflineCatalog);

        var sql = PipelineFileStats.Aggregate(db, Guid.NewGuid()).ToQueryString();

        // One statement, every aggregate pushed down: count, total, extremes, the squares that yield the standard
        // deviation, the row total, and the modified bounds.
        Assert.Contains("COUNT_BIG(*)", sql, StringComparison.Ordinal);
        Assert.Contains("SUM(", sql, StringComparison.Ordinal);
        Assert.Contains("MIN(", sql, StringComparison.Ordinal);
        Assert.Contains("MAX(", sql, StringComparison.Ordinal);
        Assert.Contains("float", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MiddleSizes_TakesTheMiddleOfTheSizeOrder_InSql()
    {
        using var db = CatalogDatabase.Create(OfflineCatalog);

        // An even count straddles two middle rows; an odd count lands on one.
        var even = PipelineFileStats.MiddleSizes(db, Guid.NewGuid(), 10).ToQueryString();
        var odd = PipelineFileStats.MiddleSizes(db, Guid.NewGuid(), 11).ToQueryString();

        Assert.Contains("ORDER BY", even, StringComparison.Ordinal);
        Assert.Contains("OFFSET", even, StringComparison.Ordinal);
        Assert.Contains("FETCH NEXT", even, StringComparison.Ordinal);
        Assert.Contains("ORDER BY", odd, StringComparison.Ordinal);
    }

    [Fact]
    public void RecentFiles_TakesTheNewestWindow_InSql()
    {
        using var db = CatalogDatabase.Create(OfflineCatalog);

        var sql = PipelineFileStats.RecentFiles(db, Guid.NewGuid()).ToQueryString();

        Assert.Contains("ORDER BY", sql, StringComparison.Ordinal);
        Assert.Contains("DESC", sql, StringComparison.Ordinal);
        Assert.Contains(PipelineFileStats.RecentWindow.ToString(System.Globalization.CultureInfo.InvariantCulture), sql, StringComparison.Ordinal);
    }
}
