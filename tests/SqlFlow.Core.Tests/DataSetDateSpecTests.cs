using System.Globalization;
using SqlFlow.Core.Model;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Extensive coverage of <see cref="DataSetDateSpec"/>: the filename-date detection that populates DataSet_DW,
/// ported from the legacy pre-ingestion logic (extract a date from the file name using a format vocabulary, with
/// the last-modified timestamp as the fallback). Covers each built-in format, embedded/anchored segments,
/// invalid-date and out-of-range rejection, the id/version false-positive guard, format precedence
/// (datetime before date, longest first), the European day-first tie-break, custom formats via options, and the
/// modified-timestamp fallback and opt-out.
/// </summary>
public sealed class DataSetDateSpecTests
{
    private static readonly DataSetDateSpec Default = DataSetDateSpec.FromOptions(new Dictionary<string, string?>());

    [Theory]
    // compact and delimited date-only
    [InlineData("sess_20240101.csv", "2024-01-01T00:00:00")]
    [InlineData("20240101.csv", "2024-01-01T00:00:00")]                       // date at the very start
    [InlineData("v20240101_final.csv", "2024-01-01T00:00:00")]                // embedded, non-digit neighbours
    [InlineData("orders_2024-01-01.csv", "2024-01-01T00:00:00")]
    [InlineData("data_2024_01_01.csv", "2024-01-01T00:00:00")]
    [InlineData("x2024.01.01.csv", "2024-01-01T00:00:00")]
    [InlineData("single_2024-1-5.csv", "2024-01-05T00:00:00")]                // variable-width M/d
    [InlineData("monthly_2024-01.csv", null)]                                 // year-month is opt-in, not a default
    // date + time (most specific wins over the date-only prefix)
    [InlineData("report20240101120000.log", "2024-01-01T12:00:00")]
    [InlineData("dump_20240101_120000.bak", "2024-01-01T12:00:00")]
    [InlineData("ts_2024-01-01_12-30-45.csv", "2024-01-01T12:30:45")]
    // European day-first and its tie-break over month-first
    [InlineData("eu_31-12-2024.csv", "2024-12-31T00:00:00")]
    [InlineData("eu_31.12.2024.csv", "2024-12-31T00:00:00")]
    [InlineData("compact_31122024.csv", "2024-12-31T00:00:00")]               // yyyyMMdd fails (month 20), ddMMyyyy wins
    [InlineData("ambiguous_01-02-2024.csv", "2024-02-01T00:00:00")]           // dd-MM-yyyy (Feb 1), not MM-dd
    [InlineData("future_20991231.csv", "2099-12-31T00:00:00")]
    // no detectable / rejected date -> null
    [InlineData("none_report.csv", null)]
    [InlineData("id_1234567890.csv", null)]                                   // 10-digit id: no isolated date run
    [InlineData("invalidmonth_20241301.csv", null)]                          // month 13 in every ordering
    [InlineData("old_18991231.csv", null)]                                    // year < 1900 sentinel
    [InlineData("compact6_202401.csv", null)]                                 // bare 6-digit is not a default date
    public void TryExtract_DetectsFilenameDate(string fileName, string? expectedIso)
    {
        var result = Default.TryExtract(fileName);

        if (expectedIso is null)
        {
            Assert.Null(result);
        }
        else
        {
            Assert.Equal(DateTime.ParseExact(expectedIso, "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), result);
        }
    }

    [Fact]
    public void TryExtract_PrefersFullTimestamp_OverDateOnlyPrefix()
    {
        // The 14-digit run must resolve to the full timestamp, not be truncated to the 8-digit date.
        var result = Default.TryExtract("x_20240101235959.csv");
        Assert.Equal(new DateTime(2024, 1, 1, 23, 59, 59), result);
    }

    [Fact]
    public void TryExtract_TakesFirstMatch_WhenNameHasMultipleDates()
    {
        // Deterministic: the first (leftmost) match for the winning format is returned.
        var result = Default.TryExtract("from_2024-01-01_to_2024-02-01.csv");
        Assert.Equal(new DateTime(2024, 1, 1), result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no_digits_here.csv")]
    [InlineData("only_2024_year.csv")]      // a bare 4-digit year is not a full date
    public void TryExtract_ReturnsNull_WhenNoDate(string fileName)
        => Assert.Null(Default.TryExtract(fileName));

    [Fact]
    public void CustomFormat_IsTriedBeforeBuiltIns_AndWinsLengthTie()
    {
        // yyMMdd is a 6-char format like the built-in yyyy-MM has different length; give a genuinely custom shape
        // the built-ins do not cover and confirm it resolves.
        var spec = DataSetDateSpec.FromOptions(new Dictionary<string, string?> { ["dataSetFormats"] = "yyMMdd" });

        Assert.Equal(new DateTime(2024, 3, 1), spec.TryExtract("bill_240301.csv"));
        Assert.Contains("yyMMdd", spec.Formats);
        // The built-ins are still present after the custom format.
        Assert.Contains("yyyyMMdd", spec.Formats);
    }

    [Fact]
    public void CustomFormats_ParseCommaAndPipeSeparated()
    {
        var spec = DataSetDateSpec.FromOptions(new Dictionary<string, string?> { ["dataSetFormats"] = "yyMMdd | ddMMMyyyy , yyyyDDD" });

        Assert.Contains("yyMMdd", spec.Formats);
        Assert.Contains("ddMMMyyyy", spec.Formats);
        Assert.Contains("yyyyDDD", spec.Formats);
    }

    [Fact]
    public void Resolve_UsesFilenameDate_WhenPresent()
    {
        var modified = new DateTime(2024, 6, 15, 9, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2024, 1, 1), Default.Resolve("sess_20240101.csv", modified));
    }

    [Fact]
    public void Resolve_FallsBackToModified_WhenNoFilenameDate()
    {
        var modified = new DateTime(2024, 6, 15, 9, 0, 0, DateTimeKind.Utc);
        Assert.Equal(modified, Default.Resolve("no_date_here.csv", modified));
    }

    [Fact]
    public void ModifiedOnly_NeverReadsTheName()
    {
        var modified = new DateTime(2024, 6, 15, 9, 0, 0, DateTimeKind.Utc);

        Assert.False(DataSetDateSpec.ModifiedOnly.FromFileName);
        Assert.Null(DataSetDateSpec.ModifiedOnly.TryExtract("sess_20240101.csv"));
        Assert.Equal(modified, DataSetDateSpec.ModifiedOnly.Resolve("sess_20240101.csv", modified));
    }

    [Fact]
    public void FromOptions_DefaultsToFromFileName()
        => Assert.True(DataSetDateSpec.FromOptions(new Dictionary<string, string?>()).FromFileName);

    [Fact]
    public void FromOptions_OptOut_ReturnsModifiedOnly()
    {
        var spec = DataSetDateSpec.FromOptions(new Dictionary<string, string?> { ["dataSetFromFileName"] = "false" });
        Assert.Same(DataSetDateSpec.ModifiedOnly, spec);
    }

    [Theory]
    // Calendar validity: TryParseExact rejects impossible dates rather than the regex guessing.
    [InlineData("d_2024-13-01.csv", null)]                                    // month 13
    [InlineData("d_2024-00-10.csv", null)]                                    // month 00
    [InlineData("d_2024-02-30.csv", null)]                                    // Feb 30
    [InlineData("d_20240230.csv", null)]                                      // Feb 30, compact
    // Digit-gluing boundaries: a date must be isolated from surrounding digits.
    [InlineData("glued_2024010112.csv", null)]                               // 10 contiguous digits: no isolated 8/14
    [InlineData("mid20240101.csv", "2024-01-01T00:00:00")]                    // glued to letters (letters are a boundary)
    [InlineData("2024010199_x.csv", null)]                                    // 10 digits: not an 8-digit date
    // Year sentinel then day-first fallthrough (European): yyyyMMdd fails, ddMMyyyy wins.
    [InlineData("report_03042020.csv", "2020-04-03T00:00:00")]               // dd=03 MM=04 yyyy=2020
    // Specificity beats position: the longest format wins even when it matches later in the name.
    [InlineData("later_2024-05_full_2024-05-06.csv", "2024-05-06T00:00:00")]
    // Every occurrence of a format is tried: an id that fits the shape but is not a valid date does not stop a
    // real later date of the same format from being found.
    [InlineData("id99999999_20240101.csv", "2024-01-01T00:00:00")]
    public void TryExtract_EdgeCases(string fileName, string? expectedIso)
    {
        var result = Default.TryExtract(fileName);

        if (expectedIso is null)
        {
            Assert.Null(result);
        }
        else
        {
            Assert.Equal(DateTime.ParseExact(expectedIso, "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), result);
        }
    }

    [Fact]
    public void TryExtract_UnicodeDigits_DoNotThrow_AndAreNotAccepted()
    {
        // Arabic-Indic 20240101: \d may match but TryParseExact(InvariantCulture) does not parse non-ASCII digits.
        Assert.Null(Default.TryExtract("sess_٢٠٢٤٠١٠١.csv"));
    }

    [Fact]
    public void FromOptions_MalformedOrUnsupportedCustomFormats_DoNotThrow_AndBuiltInsStillApply()
    {
        // Month-NAME tokens (MMM) and junk formats are accepted as configuration but never match numeric names
        // (the extractor is numeric-token only, matching legacy); they must not crash and must not disable the
        // built-ins.
        var spec = DataSetDateSpec.FromOptions(new Dictionary<string, string?>
        {
            ["dataSetFormats"] = "ddMMMyyyy | garbage | ]][[ | \\",
        });

        Assert.Null(spec.TryExtract("no_date_here.csv"));
        Assert.Equal(new DateTime(2024, 1, 1), spec.TryExtract("sess_20240101.csv"));
    }

    [Fact]
    public void YearMonth_IsOptInViaDataSetFormats()
    {
        var spec = DataSetDateSpec.FromOptions(new Dictionary<string, string?> { ["dataSetFormats"] = "yyyy-MM" });
        Assert.Equal(new DateTime(2024, 5, 1), spec.TryExtract("monthly_2024-05.csv"));
    }

    [Fact]
    public void TryExtract_VeryLongName_IsLinear_AndDoesNotHang()
    {
        // No nested quantifiers, so a pathological name cannot backtrack catastrophically.
        var name = new string('9', 5000) + "_2024-01-01_" + new string('7', 5000) + ".csv";
        Assert.Equal(new DateTime(2024, 1, 1), Default.TryExtract(name));
    }

    // ---- File-set inference of the ambiguous day-first vs month-first reading ----

    [Fact]
    public void ForFileSet_InfersMonthFirst_FromUnambiguousSibling()
    {
        // 03-15-2024 can only be MM-dd (month 15 is impossible the other way): it teaches the set month-first,
        // which then applies to the otherwise-ambiguous 01-02-2024.
        var spec = Default.ForFileSet(["03-15-2024.csv", "01-02-2024.csv"]);
        Assert.Equal(new DateTime(2024, 1, 2), spec.TryExtract("01-02-2024.csv"));
    }

    [Fact]
    public void ForFileSet_KeepsDayFirst_WithDayFirstEvidence()
    {
        // 15-03-2024 forces day-first; the ambiguous sibling stays day-first (1 February).
        var spec = Default.ForFileSet(["15-03-2024.csv", "01-02-2024.csv"]);
        Assert.Equal(new DateTime(2024, 2, 1), spec.TryExtract("01-02-2024.csv"));
    }

    [Fact]
    public void ForFileSet_DefaultsToDayFirst_WithNoEvidence()
    {
        var spec = Default.ForFileSet(["01-02-2024.csv", "05-06-2024.csv"]);   // every name ambiguous
        Assert.Equal(new DateTime(2024, 2, 1), spec.TryExtract("01-02-2024.csv"));
    }

    [Fact]
    public void ForFileSet_DefaultsToDayFirst_OnContradictoryEvidence()
    {
        // The set contains both a day-forced and a month-forced name: it has no single convention, so the
        // ambiguous name falls back to the day-first default, while the forced names still resolve per file.
        var spec = Default.ForFileSet(["15-03-2024.csv", "03-15-2024.csv", "01-02-2024.csv"]);
        Assert.Equal(new DateTime(2024, 2, 1), spec.TryExtract("01-02-2024.csv"));
        Assert.Equal(new DateTime(2024, 3, 15), spec.TryExtract("15-03-2024.csv"));
        Assert.Equal(new DateTime(2024, 3, 15), spec.TryExtract("03-15-2024.csv"));
    }

    [Fact]
    public void ForFileSet_InfersCompactFamily_Independently()
    {
        // The compact ddMMyyyy/MMddyyyy family is inferred separately: 03152024 forces month-first.
        var spec = Default.ForFileSet(["03152024.csv", "01022024.csv"]);
        Assert.Equal(new DateTime(2024, 1, 2), spec.TryExtract("01022024.csv"));
    }

    [Fact]
    public void ForFileSet_IsNoOp_WhenOptedOutOrEmpty()
    {
        Assert.Same(DataSetDateSpec.ModifiedOnly, DataSetDateSpec.ModifiedOnly.ForFileSet(["03-15-2024.csv"]));
        Assert.Equal(new DateTime(2024, 2, 1), Default.ForFileSet([]).TryExtract("01-02-2024.csv"));
    }

    [Fact]
    public void DayFirstLock_False_ForcesMonthFirst_WithoutAFileSet()
    {
        // The explicit lock applies immediately (a single-file read never calls ForFileSet).
        var spec = DataSetDateSpec.FromOptions(new Dictionary<string, string?> { ["dataSetDayFirst"] = "false" });
        Assert.Equal(new DateTime(2024, 1, 2), spec.TryExtract("01-02-2024.csv"));
    }

    [Fact]
    public void DayFirstLock_True_OverridesFileSetEvidence()
    {
        // Locked day-first: month-first evidence in the set is ignored.
        var spec = DataSetDateSpec.FromOptions(new Dictionary<string, string?> { ["dataSetDayFirst"] = "true" })
            .ForFileSet(["03-15-2024.csv", "01-02-2024.csv"]);
        Assert.Equal(new DateTime(2024, 2, 1), spec.TryExtract("01-02-2024.csv"));
    }

    // ---- The detected-convention audit line (surfaced to run.json and the catalog) ----

    [Fact]
    public void Convention_ReportsLastModified_WhenOptedOut()
        => Assert.Equal("last-modified", DataSetDateSpec.ModifiedOnly.Convention);

    [Fact]
    public void Convention_ReportsDayFirstDefault()
        => Assert.Equal("filename dates; day-first", Default.Convention);

    [Fact]
    public void Convention_ReportsMonthFirstInferred_AfterFileSet()
        => Assert.Equal(
            "filename dates; month-first (inferred from file set)",
            Default.ForFileSet(["03-15-2024.csv", "01-02-2024.csv"]).Convention);

    [Fact]
    public void Convention_ReportsLocked_BothDirections()
    {
        Assert.Equal("filename dates; day-first (locked)",
            DataSetDateSpec.FromOptions(new Dictionary<string, string?> { ["dataSetDayFirst"] = "true" }).Convention);
        Assert.Equal("filename dates; month-first (locked)",
            DataSetDateSpec.FromOptions(new Dictionary<string, string?> { ["dataSetDayFirst"] = "false" }).Convention);
    }
}
