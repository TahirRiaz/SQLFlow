using SqlFlow.Core;
using SqlFlow.Core.Ingestion;
using Xunit;

namespace SqlFlow.Tests;

public sealed class ColumnNameCleanerTests
{
    private static readonly IColumnNameCleaner Cleaner = new DefaultColumnNameCleaner();

    [Fact]
    public void Disabled_ReturnsRawUnchanged()
    {
        var raw = new[] { "Order #", "A B" };
        Assert.Same(raw, Cleaner.Clean(raw, new SchemaSyncPolicy { CleanColumnNames = false }));
    }

    [Fact]
    public void Enabled_WithoutRegex_FallsBackToTheLegacyDefault()
    {
        // Legacy fell back to flw.SysCFG.ColCleanupSQLRegExp when the per-flow regex was blank; the canonical
        // default keeps letters/digits/underscore and the Norwegian letters, removing everything else.
        var policy = new SchemaSyncPolicy { CleanColumnNames = true };
        Assert.Equal(new[] { "Ordre", "blåbær", "X" }, Cleaner.Clean(["Ordre #", "blåbær!", "X"], policy));
    }

    [Fact]
    public void RemovesInvalidChars_WhenReplacementBlank()
    {
        var policy = new SchemaSyncPolicy { CleanColumnNames = true, CleanColumnNameRegex = "[^A-Za-z0-9_]" };
        Assert.Equal(new[] { "OrderNo", "Amount" }, Cleaner.Clean(["Order No", "Amount$"], policy));
    }

    [Fact]
    public void ReplacesInvalidChars_WithConfiguredString()
    {
        var policy = new SchemaSyncPolicy { CleanColumnNames = true, CleanColumnNameRegex = "[^A-Za-z0-9_]", ReplaceInvalidCharsWith = "_" };
        Assert.Equal(new[] { "Order_No" }, Cleaner.Clean(["Order No"], policy));
    }

    [Fact]
    public void EmptyResult_BecomesPlaceholder()
    {
        var policy = new SchemaSyncPolicy { CleanColumnNames = true, CleanColumnNameRegex = "[^A-Za-z0-9_]" };
        Assert.Equal(new[] { "EmptyColumnName" }, Cleaner.Clean(["###"], policy));
    }

    [Fact]
    public void Collisions_AreDeduplicated()
    {
        var policy = new SchemaSyncPolicy { CleanColumnNames = true, CleanColumnNameRegex = "[^A-Za-z0-9_]" };
        // "Order#" and "Order!" both clean to "Order".
        Assert.Equal(new[] { "Order", "Order1", "Order2" }, Cleaner.Clean(["Order#", "Order!", "Order "], policy));
    }
}
