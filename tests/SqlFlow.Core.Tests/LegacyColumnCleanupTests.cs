using SqlFlow.Core.Ingestion;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The backwards-compatibility contract for column-name cleanup: a V3 file flow must produce the EXACT target
/// column names a legacy SQLFlow install produced (legacy Shared.cs), or schema-sync would treat every
/// existing column as missing. These tests pin the canonical default character set and the file algorithm
/// (regex to underscore, ordinal-suffix dedup) so an encoding accident or a refactor cannot silently change
/// the names estates were built with.
/// </summary>
public sealed class LegacyColumnCleanupTests
{
    [Fact]
    public void DefaultRegex_IsTheExactCanonicalCharacterSet()
    {
        // The value the legacy installs used (flw.SysCFG.ColCleanupSQLRegExp). Spelled with \u escapes here
        // so the assertion is independent of this test file's own encoding.
        const string expected = "[^a-zA-Z0-9æøåÆØÅ_]";
        Assert.Equal(expected, LegacyColumnCleanup.DefaultCleanupRegex);
        Assert.Equal("_", LegacyColumnCleanup.FileReplacement);
    }

    [Fact]
    public void File_ReplacesEveryInvalidCharacterWithUnderscore()
    {
        var cleaned = LegacyColumnCleanup.CleanFileColumnNames(
            ["Row ID", "Order ID", "Sub-Category", "Amount ($)", "Customer.Name", "A/B"]);

        Assert.Equal(
            ["Row_ID", "Order_ID", "Sub_Category", "Amount____", "Customer_Name", "A_B"],
            cleaned);
    }

    [Fact]
    public void File_KeepsTheNorwegianLetters_AndUnderscoreAndDigits()
    {
        // æøåÆØÅ, ASCII letters/digits, and underscore survive; nothing else does.
        var cleaned = LegacyColumnCleanup.CleanFileColumnNames(["Blåbær_2", "Ærlig Øre", "Måned#3"]);

        Assert.Equal(["Blåbær_2", "Ærlig_Øre", "Måned_3"], cleaned);
    }

    [Fact]
    public void File_DeduplicatesByAppendingTheColumnOrdinal()
    {
        // "A B" and "A#B" both clean to "A_B"; legacy appends the colliding column's zero-based ordinal.
        var cleaned = LegacyColumnCleanup.CleanFileColumnNames(["A B", "A#B", "A.B"]);

        Assert.Equal(["A_B", "A_B1", "A_B2"], cleaned);
    }

    [Fact]
    public void File_CollisionIsCaseInsensitive()
    {
        // SQL Server columns are case-insensitive by default, matching legacy's ContainsKeyIgnoreCase.
        var cleaned = LegacyColumnCleanup.CleanFileColumnNames(["Total", "TOTAL", "total"]);

        Assert.Equal(["Total", "TOTAL1", "total2"], cleaned);
    }

    [Fact]
    public void File_AllInvalidName_BecomesUnderscores_NotEmpty()
    {
        // The file path has no "EmptyColumnName" placeholder (that is the relational path); "##" -> "__".
        Assert.Equal(["__"], LegacyColumnCleanup.CleanFileColumnNames(["##"]));
    }

    [Fact]
    public void File_AlreadyCleanNames_AreUnchanged()
    {
        var names = new[] { "OrderId", "Customer", "Amount_DW", "Column1" };
        Assert.Equal(names, LegacyColumnCleanup.CleanFileColumnNames(names));
    }

    [Fact]
    public void File_PreservesOrder_SoPositionalReadsStayAligned()
    {
        var cleaned = LegacyColumnCleanup.CleanFileColumnNames(["c 0", "c 1", "c 2", "c 3"]);
        Assert.Equal(["c_0", "c_1", "c_2", "c_3"], cleaned);
    }

    [Fact]
    public void File_NullEntry_IsTreatedAsEmpty()
    {
        Assert.Equal([string.Empty], LegacyColumnCleanup.CleanFileColumnNames([null!]));
    }

    [Fact]
    public void File_CustomRegexOverride_IsHonored()
    {
        // A per-flow override (legacy CleanColumnNameSQLRegExp) can treat the Norwegian letters as invalid;
        // the file path always REPLACES with underscore (it never removes), so å and æ become "_".
        var cleaned = LegacyColumnCleanup.CleanFileColumnNames(["Blåbær"], "[^A-Za-z0-9_]");
        Assert.Equal(["Bl_b_r"], cleaned);
    }
}
