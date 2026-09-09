using SqlFlow.Core.Ingestion;
using SqlFlow.SqlServer.Ingestion;
using Xunit;

namespace SqlFlow.Tests;

public sealed class CanonicalIndexPlannerTests
{
    private static RelationalObject Target() => new() { Database = "db", Schema = "dbo", Name = "Fact" };

    [Fact]
    public void KeyDateAndUpdated_ProduceThreeGuardedIndexes()
    {
        var statements = CanonicalIndexPlanner.Plan(Target(), ["Id"], "OrderDate", null, hasUpdatedDateColumn: true, columnStore: false, hasIdentityPrimaryKey: false);

        Assert.Equal(3, statements.Count);
        Assert.Contains(statements, s => s.Contains("UNIQUE NONCLUSTERED INDEX [NCI_KeyColumn]", StringComparison.Ordinal) && s.Contains("([Id])", StringComparison.Ordinal));
        Assert.Contains(statements, s => s.Contains("NONCLUSTERED INDEX [NCI_DateColumn]", StringComparison.Ordinal) && s.Contains("([OrderDate])", StringComparison.Ordinal));
        Assert.Contains(statements, s => s.Contains("[NCI_UpdatedDate_DW]", StringComparison.Ordinal) && s.Contains("([UpdatedDate_DW])", StringComparison.Ordinal));
        Assert.All(statements, s => Assert.Contains("IF NOT EXISTS (SELECT 1 FROM sys.indexes", s, StringComparison.Ordinal));
    }

    [Fact]
    public void NoKeyColumns_OmitsKeyIndex()
        => Assert.Empty(CanonicalIndexPlanner.Plan(Target(), [], null, null, hasUpdatedDateColumn: false, columnStore: false, hasIdentityPrimaryKey: false));

    [Fact]
    public void DataSetColumn_EmitsIndex()
    {
        var statements = CanonicalIndexPlanner.Plan(Target(), ["Id"], null, "Batch", hasUpdatedDateColumn: false, columnStore: false, hasIdentityPrimaryKey: false);
        Assert.Contains(statements, s => s.Contains("[NCI_DataSetColumn]", StringComparison.Ordinal) && s.Contains("([Batch])", StringComparison.Ordinal));
    }

    [Fact]
    public void Columnstore_WithoutIdentity_EmitsClusteredColumnstore()
    {
        var statements = CanonicalIndexPlanner.Plan(Target(), [], null, null, hasUpdatedDateColumn: false, columnStore: true, hasIdentityPrimaryKey: false);
        Assert.Contains(statements, s => s.Contains("CREATE CLUSTERED COLUMNSTORE INDEX [CCSI_Fact]", StringComparison.Ordinal));
    }

    [Fact]
    public void Columnstore_WithIdentity_IsSuppressed()
    {
        var statements = CanonicalIndexPlanner.Plan(Target(), ["Id"], null, null, hasUpdatedDateColumn: false, columnStore: true, hasIdentityPrimaryKey: true);
        Assert.DoesNotContain(statements, s => s.Contains("COLUMNSTORE", StringComparison.Ordinal));
    }

    [Fact]
    public void ReloadColumn_EmitsIndexSoThePurgeSeeks()
    {
        var statements = CanonicalIndexPlanner.Plan(
            Target(), ["Id"], null, null, hasUpdatedDateColumn: false, columnStore: false, hasIdentityPrimaryKey: false,
            reloadColumn: "FileName_DW");
        Assert.Contains(statements, s => s.Contains("[NCI_ReloadColumn]", StringComparison.Ordinal) && s.Contains("([FileName_DW])", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Id", null, null)]       // already the key's leading column
    [InlineData(null, "FileDate", null)] // already the date column
    [InlineData(null, null, "FileDate")] // already the dataset column
    public void ReloadColumn_AlreadyCoveredByAnotherIndex_IsNotDuplicated(string? keyName, string? dateColumn, string? dataSetColumn)
    {
        var keys = keyName is null ? (IReadOnlyList<string>)[] : [keyName];
        var reloadColumn = keyName ?? dateColumn ?? dataSetColumn!;
        var statements = CanonicalIndexPlanner.Plan(
            Target(), keys, dateColumn, dataSetColumn, hasUpdatedDateColumn: false, columnStore: false, hasIdentityPrimaryKey: false,
            reloadColumn: reloadColumn);
        Assert.DoesNotContain(statements, s => s.Contains("[NCI_ReloadColumn]", StringComparison.Ordinal));
    }

    [Fact]
    public void ReloadColumn_Unset_EmitsNoReloadIndex()
    {
        var statements = CanonicalIndexPlanner.Plan(Target(), ["Id"], null, null, hasUpdatedDateColumn: false, columnStore: false, hasIdentityPrimaryKey: false);
        Assert.DoesNotContain(statements, s => s.Contains("[NCI_ReloadColumn]", StringComparison.Ordinal));
    }
}
