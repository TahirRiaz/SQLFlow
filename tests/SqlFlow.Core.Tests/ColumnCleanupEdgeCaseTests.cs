using SqlFlow.Core;
using SqlFlow.Core.Ingestion;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Additional, non-overlapping edge-case coverage for the two column-name cleanup paths, the relational
/// opt-in <see cref="DefaultColumnNameCleaner"/> (policy-driven regex, replacement, empty placeholder, numeric
/// dedup) and the always-on file path <see cref="LegacyColumnCleanup.CleanFileColumnNames"/> (regex to
/// underscore, ordinal-suffix dedup). These probe boundaries the existing
/// <see cref="ColumnNameCleanerTests"/> and <see cref="LegacyColumnCleanupTests"/> do not already pin:
/// the underscore re-disambiguation branch when an ordinal-suffixed name itself collides, interactions
/// between literal and generated dedup names, surrogate-pair (emoji) stripping versus per-code-unit
/// underscoring, whitespace and control characters, multi-character and digit-bearing replacements, leading
/// digits, case preservation, custom regexes, invalid-regex failure on both paths, and empty inputs. Every
/// test is pure in-memory (no database, no network) and deterministic (fixed inputs only).
/// </summary>
public sealed class ColumnCleanupEdgeCaseTests
{
    private static readonly IColumnNameCleaner RelationalCleaner = new DefaultColumnNameCleaner();

    // Splits a pipe-delimited list into its parts; an empty string yields an empty array (not a single empty
    // element), so a test can express "no columns" as "". A literal single empty column is written as "|"
    // is avoided; the dedicated empty-name tests pass arrays directly instead.
    private static string[] Parts(string pipeDelimited)
        => pipeDelimited.Length == 0 ? [] : pipeDelimited.Split('|');

    // Convenience wrapper for the relational (opt-in) path with cleanup enabled and an optional regex/replacement.
    private static IReadOnlyList<string> Relational(string[] raw, string? regex = null, string? replacement = null)
        => RelationalCleaner.Clean(raw, new SchemaSyncPolicy
        {
            CleanColumnNames = true,
            CleanColumnNameRegex = regex,
            ReplaceInvalidCharsWith = replacement,
        });

    // ---------------------------------------------------------------------------------------------------
    // File path (always-on): regex to underscore, case-insensitive ordinal-suffix dedup.
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    // A name that collides AND whose ordinal-suffixed form ALSO already exists falls through to the
    // "cleaned + _ + extra" re-disambiguation branch; "A" at index 3 wants "A3", which the literal "A3"
    // already took, so it becomes "A_4". This branch is not exercised by the existing tests.
    [InlineData("A|A3|z|A", "A|A3|z|A_4")]
    // The ordinal-suffixed name itself collides with a later raw value: index 1 "A" becomes "A1"
    // (the literal at index 2), so index 2's "A1" must move to "A12".
    [InlineData("A|A|A1", "A|A1|A12")]
    // A pre-existing "c1" forces the second "c" (index 1) to "c1", so the literal "c1" at index 2 becomes "c12".
    [InlineData("c|c|c1", "c|c1|c12")]
    public void File_OrdinalSuffix_ReDisambiguatesWhenSuffixedNameAlreadyTaken(string raw, string expected)
        => Assert.Equal(Parts(expected), LegacyColumnCleanup.CleanFileColumnNames(Parts(raw)));

    [Fact]
    public void File_FourIdenticalNames_GetSequentialOrdinalSuffixes()
    {
        // Four copies of "x": the first stays, the rest take their own ordinal index as the suffix.
        Assert.Equal(["x", "x1", "x2", "x3"], LegacyColumnCleanup.CleanFileColumnNames(["x", "x", "x", "x"]));
    }

    [Fact]
    public void File_EmptyHeaderName_StaysEmpty_NoPlaceholder()
    {
        // The file path has no "EmptyColumnName" placeholder; an already-empty header stays empty (unlike the
        // relational path). This differs from the existing "##" -> "__" case because nothing is replaced here.
        Assert.Equal([string.Empty], LegacyColumnCleanup.CleanFileColumnNames([string.Empty]));
    }

    [Fact]
    public void File_WhitespaceOnlyName_BecomesUnderscoresPerCharacter()
    {
        // Three spaces are three invalid characters, each replaced by one underscore: "___".
        Assert.Equal(["___"], LegacyColumnCleanup.CleanFileColumnNames(["   "]));
    }

    [Fact]
    public void File_LeadingAndTrailingSpaces_AreUnderscored_NotTrimmed()
    {
        // The cleaner never trims; surrounding spaces become underscores, so " x " -> "_x_".
        Assert.Equal(["_x_"], LegacyColumnCleanup.CleanFileColumnNames([" x "]));
    }

    [Fact]
    public void File_TabAndNewlineControlChars_BecomeUnderscores()
    {
        // Control whitespace (tab, newline) is invalid just like a space: each becomes one underscore.
        Assert.Equal(["A_B_C"], LegacyColumnCleanup.CleanFileColumnNames(["A\tB\nC"]));
    }

    [Fact]
    public void File_EmojiSurrogatePair_BecomesTwoUnderscores()
    {
        // A non-BMP emoji is a UTF-16 surrogate PAIR (two code units); the character-class regex matches each
        // unit, so a single emoji yields TWO underscores. This pins the per-code-unit behavior.
        Assert.Equal(["A__B"], LegacyColumnCleanup.CleanFileColumnNames(["A\U0001F600B"]));
    }

    [Theory]
    // Non-Nordic Latin-1 letters (German sharp s, e-acute, u-umlaut) are NOT in the kept set and become "_".
    [InlineData("Straße", "Stra_e")]
    // Cyrillic, Greek, and CJK are all outside the kept ASCII + Nordic set.
    [InlineData("Привет", "______")]
    [InlineData("中文", "__")]
    public void File_NonNordicUnicodeLetters_AreUnderscored(string raw, string expected)
        => Assert.Equal([expected], LegacyColumnCleanup.CleanFileColumnNames([raw]));

    [Fact]
    public void File_CaseIsPreservedExactly_WhenNoCollision()
    {
        // Distinct casings that do not collide keep their original case verbatim.
        Assert.Equal(["CamelCase", "lower", "UPPER"], LegacyColumnCleanup.CleanFileColumnNames(["CamelCase", "lower", "UPPER"]));
    }

    [Fact]
    public void File_LeadingDigitName_IsKept_NoIdentifierFixup()
    {
        // The cleaner does not prefix or escape an identifier that starts with a digit; digits are valid.
        Assert.Equal(["1Order", "2024Sales"], LegacyColumnCleanup.CleanFileColumnNames(["1Order", "2024Sales"]));
    }

    [Fact]
    public void File_AllNordicName_IsFullyPreserved()
    {
        // Every Nordic letter in both cases survives unchanged on the file path with the default regex.
        Assert.Equal(["æøåÆØÅ"], LegacyColumnCleanup.CleanFileColumnNames(["æøåÆØÅ"]));
    }

    [Fact]
    public void File_VeryLongName_IsCleanedCharByChar_WithoutTruncation()
    {
        // A 4000-character header alternating a valid letter and an invalid space cleans to the same length
        // (no truncation), with every space turned into an underscore.
        var raw = string.Concat(Enumerable.Repeat("a ", 2000)).TrimEnd();
        var expected = string.Concat(Enumerable.Repeat("a_", 2000)).TrimEnd('_');

        var cleaned = LegacyColumnCleanup.CleanFileColumnNames([raw]);

        Assert.Single(cleaned);
        Assert.Equal(expected, cleaned[0]);
        Assert.Equal(raw.Length, cleaned[0].Length);
    }

    [Fact]
    public void File_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(LegacyColumnCleanup.CleanFileColumnNames([]));
    }

    [Fact]
    public void File_OutputIsIndexAlignedWithInput()
    {
        var raw = new[] { "Col A", "Col B", "Col C" };
        var cleaned = LegacyColumnCleanup.CleanFileColumnNames(raw);
        Assert.Equal(raw.Length, cleaned.Count);
    }

    [Fact]
    public void File_CustomRegexKeepingDot_LeavesDotInPlace()
    {
        // A per-flow override that treats only spaces as invalid (keeping the dot) is honored; the file path
        // still REPLACES with underscore, so "a.b c" -> "a.b_c".
        Assert.Equal(["a.b_c"], LegacyColumnCleanup.CleanFileColumnNames(["a.b c"], "[^A-Za-z0-9_.]"));
    }

    [Fact]
    public void File_CustomRegexTreatingNordicAsInvalid_UnderscoresThem()
    {
        // An override without the Nordic letters turns them into underscores (the file path never removes).
        Assert.Equal(["_re"], LegacyColumnCleanup.CleanFileColumnNames(["Åre"], "[^A-Za-z0-9_]"));
    }

    [Fact]
    public void File_EmptyRegexOverride_FallsBackToTheDefaultCharacterSet()
    {
        // An empty (not null) override is treated as "use the default", so the Nordic letters survive.
        Assert.Equal(["Blåbær"], LegacyColumnCleanup.CleanFileColumnNames(["Blåbær"], string.Empty));
    }

    [Fact]
    public void File_InvalidRegexOverride_ThrowsSqlFlowException()
    {
        // A malformed override surfaces as a typed SqlFlowException (not a raw ArgumentException) on the file path.
        var ex = Assert.Throws<SqlFlowException>(() => LegacyColumnCleanup.CleanFileColumnNames(["x"], "["));
        Assert.Contains("column-cleanup regex", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void File_NullInput_Throws()
    {
        // The public contract guards its argument: a null name list is rejected, not silently treated as empty.
        Assert.Throws<ArgumentNullException>(() => LegacyColumnCleanup.CleanFileColumnNames(null!));
    }

    // ---------------------------------------------------------------------------------------------------
    // Relational (opt-in) path: DefaultColumnNameCleaner via SchemaSyncPolicy.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Relational_DefaultRegex_PreservesNordicLetters()
    {
        // With cleanup on but no explicit regex the canonical default is used, which keeps the Nordic letters
        // (the existing test pins a mixed/stripped example; this pins pure-Nordic survival on this path).
        Assert.Equal(["Blåbær", "Æøå"], Relational(["Blåbær", "Æøå"]));
    }

    [Fact]
    public void Relational_DefaultRegex_StripsInnerSpaces()
    {
        // The default regex REMOVES invalid characters (no replacement configured), so inner spaces vanish
        // rather than becoming underscores: "A B C" -> "ABC".
        Assert.Equal(["ABC"], Relational(["A B C"]));
    }

    [Fact]
    public void Relational_DefaultRegex_StripsTabAndNewline()
    {
        // Tab and newline are invalid and, with no replacement, are removed entirely.
        Assert.Equal(["ABC"], Relational(["A\tB\nC"]));
    }

    [Fact]
    public void Relational_DefaultRegex_StripsEmojiEntirely()
    {
        // Both surrogate code units of the emoji are removed, leaving the surrounding letters joined.
        Assert.Equal(["AB"], Relational(["A\U0001F600B"]));
    }

    [Fact]
    public void Relational_LeadingDigitName_IsKept()
    {
        // Digits are valid in the default set and the cleaner does no identifier fix-up, so a leading digit stays.
        Assert.Equal(["1col", "2nd"], Relational(["1col", "2nd"]));
    }

    [Fact]
    public void Relational_UnderscoresAndDigits_AreUntouched()
    {
        // An already-clean name with underscores and digits is returned verbatim.
        Assert.Equal(["_already_clean_1"], Relational(["_already_clean_1"]));
    }

    [Fact]
    public void Relational_NullReplacement_BehavesLikeRemoval()
    {
        // An explicit null ReplaceInvalidCharsWith is coalesced to empty, i.e. invalid characters are removed.
        Assert.Equal(["AB"], Relational(["A B"], "[^A-Za-z0-9_]", replacement: null));
    }

    [Fact]
    public void Relational_MultiCharReplacement_IsAppliedPerInvalidCharacter()
    {
        // A non-underscore replacement string is substituted for each invalid character.
        Assert.Equal(["AXB"], Relational(["A B"], "[^A-Za-z0-9_]", "X"));
    }

    [Fact]
    public void Relational_DigitReplacement_DoesNotConfuseDedup()
    {
        // Replacing a space with "1" turns "A B" into "A1B"; the literal "A1B" then collides and is bumped to
        // "A1B1" by the numeric-suffix dedup, confirming the replacement and the suffix are independent.
        Assert.Equal(["A1B", "A1B1"], Relational(["A B", "A1B"], "[^A-Za-z0-9_]", "1"));
    }

    [Fact]
    public void Relational_CollisionBetweenCleanedAndLiteral_IsDeduplicated()
    {
        // "Order#" cleans to "Order", colliding with the literal "Order"; the second becomes "Order1".
        Assert.Equal(["Order", "Order1"], Relational(["Order", "Order#"]));
    }

    [Fact]
    public void Relational_GeneratedSuffixSkipsAnExistingLiteralSuffix()
    {
        // "Order#" cleans to "Order" and collides; the natural candidate "Order1" is already a literal, so the
        // dedup advances to "Order2". This pins the interaction between literal and generated names.
        Assert.Equal(["Order", "Order1", "Order2"], Relational(["Order", "Order1", "Order#"]));
    }

    [Fact]
    public void Relational_FourIdenticalNames_GetSequentialNumericSuffixes()
    {
        // The numeric-suffix dedup counts up from 1 for repeated collisions of the same base name.
        Assert.Equal(["x", "x1", "x2", "x3"], Relational(["x", "x", "x", "x"]));
    }

    [Fact]
    public void Relational_CollisionIsCaseInsensitive()
    {
        // The dedup map is OrdinalIgnoreCase, matching SQL Server's default column identity, so casings collide.
        Assert.Equal(["Total", "TOTAL1", "total2"], Relational(["Total", "TOTAL", "total"]));
    }

    [Fact]
    public void Relational_AllInvalidName_BecomesPlaceholder()
    {
        // With no replacement the all-symbol name cleans to empty and is mapped to the "EmptyColumnName" placeholder.
        Assert.Equal(["EmptyColumnName"], Relational(["@@@"]));
    }

    [Fact]
    public void Relational_WhitespaceOnlyName_BecomesPlaceholder()
    {
        // A whitespace-only name cleans to empty under the default set and becomes the placeholder.
        Assert.Equal(["EmptyColumnName"], Relational(["   "]));
    }

    [Fact]
    public void Relational_MultipleEmptyNames_GetSuffixedPlaceholders()
    {
        // Several names that all clean to empty share the placeholder base and are de-duplicated numerically.
        Assert.Equal(["EmptyColumnName", "EmptyColumnName1", "EmptyColumnName2"], Relational(["###", "!!!", "$$$"]));
    }

    [Fact]
    public void Relational_PlaceholderCollidesWithLiteralEmptyColumnName()
    {
        // A literal column literally named "EmptyColumnName" collides with the generated placeholder; the
        // generated one moves to "EmptyColumnName1".
        Assert.Equal(["EmptyColumnName", "EmptyColumnName1"], Relational(["###", "EmptyColumnName"]));
    }

    [Fact]
    public void Relational_CustomRegexStrippingDigits_RemovesDigitsOnly()
    {
        // A custom regex can target a different class entirely; "[0-9]" removes digits and keeps letters.
        Assert.Equal(["ab"], Relational(["a1b2"], "[0-9]"));
    }

    [Fact]
    public void Relational_Disabled_ReturnsSameInstance_EvenWhenRegexConfigured()
    {
        // When cleanup is off the raw list is returned by reference, regardless of any configured regex or
        // replacement; the cleaner must not allocate or transform.
        var raw = new[] { "Order #", "A B" };
        var result = RelationalCleaner.Clean(raw, new SchemaSyncPolicy
        {
            CleanColumnNames = false,
            CleanColumnNameRegex = "[^A-Za-z0-9_]",
            ReplaceInvalidCharsWith = "_",
        });

        Assert.Same(raw, result);
    }

    [Fact]
    public void Relational_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(Relational([]));
    }

    [Fact]
    public void Relational_NullEntryInList_IsTreatedAsEmptyPlaceholder()
    {
        // A null name is coalesced to empty before cleaning, so it becomes the placeholder rather than throwing.
        Assert.Equal(["EmptyColumnName"], RelationalCleaner.Clean([null!], new SchemaSyncPolicy { CleanColumnNames = true }));
    }

    [Fact]
    public void Relational_InvalidRegex_ThrowsSqlFlowExceptionNamingTheSetting()
    {
        // A malformed per-flow regex surfaces as a typed SqlFlowException that names the offending setting.
        var ex = Assert.Throws<SqlFlowException>(() => Relational(["x"], "["));
        Assert.Contains("CleanColumnNameRegex", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Relational_NullRawNames_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => RelationalCleaner.Clean(null!, new SchemaSyncPolicy { CleanColumnNames = true }));
    }

    [Fact]
    public void Relational_NullPolicy_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => RelationalCleaner.Clean(["x"], null!));
    }
}
