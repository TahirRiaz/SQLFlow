using SqlFlow.Core;
using SqlFlow.Core.Acquire;
using Xunit;

namespace SqlFlow.Core.Tests;

/// <summary>
/// The lake-sourced resume point: an acquisition reads the highest watermark back out of the file names it already
/// landed, so the resume survives a redeploy, a different worker replica, and a laptop-versus-estate split.
/// </summary>
public sealed class LakeWatermarkReaderTests
{
    [Fact]
    public void Reads_the_numeric_max_from_landed_names()
    {
        // The Entur shape: one report per file, named by the id the next run resumes after. 9999999 vs 14526632
        // crosses a digit boundary, where an ordinal max would resume from the LOWER id and re-walk history forever.
        string[] landed =
        [
            "history/entur_omsetning_448641.xlsx",
            "history/entur_omsetning_9999999.xlsx",
            "history/entur_omsetning_14526632.xlsx",
        ];

        Assert.Equal("14526632", LakeWatermarkReader.Read("history/entur_omsetning_{header.x-entur-report-id}", landed));
    }

    [Fact]
    public void Ignores_names_that_do_not_match_the_template()
    {
        // A raw zone legitimately holds header sidecars, an archive subfolder, and files from an earlier naming
        // scheme. None of them may contribute a watermark candidate, or the resume point jumps to a foreign value.
        string[] landed =
        [
            "history/entur_omsetning_500.xlsx",
            "history/entur_omsetning_500.headers.json",
            "archive/2021/legacy_dump_99999999.csv",
            "history/readme.txt",
            string.Empty,
        ];

        Assert.Equal("500", LakeWatermarkReader.Read("history/entur_omsetning_{id}", landed));
    }

    [Fact]
    public void A_placeholder_never_swallows_the_extension_or_crosses_a_folder()
    {
        // The capture is bounded to one path segment and must stop before the extension the landing pipeline
        // appends, otherwise the watermark comes back as "508266.xlsx" and no later id ever compares greater.
        Assert.Equal("508266", LakeWatermarkReader.Read("report_{id}", ["report_508266.xlsx"]));
        Assert.Equal("7", LakeWatermarkReader.Read("{id}", ["7.json.gz"]));
        Assert.Null(LakeWatermarkReader.Read("report_{id}", ["report_1/nested_2.json"]));
    }

    [Fact]
    public void Nothing_landed_yields_no_watermark_so_the_seed_stays_in_force()
    {
        Assert.Null(LakeWatermarkReader.Read("history/report_{id}", []));
    }

    [Fact]
    public void Non_numeric_watermarks_order_ordinally()
    {
        // A date-partitioned landing resumes on the latest partition; ISO-8601 sorts correctly as text.
        string[] landed = ["daily/2026-07-25.json", "daily/2026-07-28.json", "daily/2026-01-03.json"];

        Assert.Equal("2026-07-28", LakeWatermarkReader.Read("daily/{yyyy-MM-dd}", landed));
    }

    [Fact]
    public void A_template_without_a_placeholder_is_rejected_at_compile()
    {
        // Nothing in the name encodes a resume point, so this configuration could only ever re-walk everything.
        // It must fail validation rather than silently do that on a schedule.
        var ex = Assert.Throws<SqlFlowException>(() => LakeWatermarkReader.Compile("history/report"));
        Assert.Contains("no '{placeholder}'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ambiguous_template_demands_an_explicit_column()
    {
        var ex = Assert.Throws<SqlFlowException>(() => LakeWatermarkReader.Compile("{yyyy}/report_{id}"));
        Assert.Contains("2 placeholders", ex.Message, StringComparison.Ordinal);

        // Naming the placeholder resolves it, and the OTHER placeholder is then just a matched segment.
        Assert.Equal("42", LakeWatermarkReader.Read("{yyyy}/report_{id}", ["2026/report_42.json"], "id"));
    }

    [Fact]
    public void An_unknown_column_names_the_available_placeholders()
    {
        var ex = Assert.Throws<SqlFlowException>(() => LakeWatermarkReader.Compile("report_{id}", "reportId"));
        Assert.Contains("'id'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Literal_braces_and_secret_references_are_not_placeholders()
    {
        // '{{' is an escaped literal brace and '${...}' is a secret reference the resolver expands before any file
        // is named; treating either as a placeholder would capture the wrong span.
        Assert.Equal("5", LakeWatermarkReader.Read("{{fixed}}_{id}", ["{fixed}_5.json"]));

        var compiled = LakeWatermarkReader.Compile("${env:PREFIX}/report_{id}");
        Assert.Single(compiled.Placeholders);
        Assert.Equal("id", compiled.Selected);
    }

    [Theory]
    [InlineData("9", "100", true)]          // digit boundary: the numeric rule, not the ordinal one
    [InlineData("100", "9", false)]
    [InlineData("2026-01-03", "2026-07-28", true)]
    [InlineData("abc", "abd", true)]
    public void Watermark_order_prefers_numeric_then_ordinal(string current, string candidate, bool advances)
    {
        Assert.Equal(advances, WatermarkOrder.IsGreater(candidate, current));
        Assert.Equal(advances ? candidate : current, WatermarkOrder.Max(current, candidate));
    }

    [Fact]
    public void Watermark_max_treats_null_and_empty_as_no_value()
    {
        Assert.Equal("5", WatermarkOrder.Max(null, "5"));
        Assert.Equal("5", WatermarkOrder.Max("5", null));
        Assert.Equal("5", WatermarkOrder.Max("5", string.Empty));
        Assert.Null(WatermarkOrder.Max(null, null));
    }
}
