using SqlFlow.Core;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The per-schedule bound on how many members of one fire execute at once. A fire enqueues its whole member set as
/// one wave-gated group, so this is the width of the running wave: it is what stops a wide source (23 flows reading
/// one modest server) from opening more connections than that server has, without throttling the estate's worker
/// concurrency and every other source with it.
///
/// These cover the declaration end (both loaders resolving the key the same way, including the default and the
/// explicit unbounded opt-out). The enforcement end is the queue's claim gate, covered by the run-queue suite.
/// </summary>
public sealed class ScheduleMaxConcurrencyTests
{
    [Theory]
    [InlineData(null, ScheduleDefaults.MaxConcurrency)] // omitted takes the product default
    [InlineData(1, 1)]                                  // strictly serial
    [InlineData(8, 8)]                                  // a source that can take more
    public void Resolve_KeepsAUsableBound(int? declared, int expected)
    {
        var resolved = ScheduleDefaults.Resolve(declared, out var invalid);

        Assert.False(invalid);
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void Resolve_TreatsZeroAsTheExplicitUnboundedOptOut()
    {
        var resolved = ScheduleDefaults.Resolve(0, out var invalid);

        Assert.False(invalid);
        Assert.Null(resolved);
    }

    [Fact]
    public void Resolve_RefusesANegativeBound()
    {
        // A bound below zero would leave every member of the fire permanently unclaimable, so it is never stored:
        // the caller is told it was invalid and the default applies.
        var resolved = ScheduleDefaults.Resolve(-3, out var invalid);

        Assert.True(invalid);
        Assert.Equal(ScheduleDefaults.MaxConcurrency, resolved);
    }

    [Fact]
    public void ScheduleLibrary_ReadsTheDeclaredBound()
    {
        var library = new YamlScheduleLibraryLoader().Parse(
            """
            schedules:
              wide:
                cron: "0 4 * * *"
                maxConcurrency: 6
              serial:
                cron: "0 5 * * *"
                maxConcurrency: 1
              unbounded:
                cron: "0 6 * * *"
                maxConcurrency: 0
              untuned:
                cron: "0 7 * * *"
            """);

        Assert.Empty(library.Warnings);
        Assert.Equal(6, Spec(library, "wide").MaxConcurrency);
        Assert.Equal(1, Spec(library, "serial").MaxConcurrency);
        Assert.Null(Spec(library, "unbounded").MaxConcurrency);
        Assert.Equal(ScheduleDefaults.MaxConcurrency, Spec(library, "untuned").MaxConcurrency);
    }

    [Fact]
    public void ScheduleLibrary_WarnsAndDefaultsOnAnUnusableBound()
    {
        var library = new YamlScheduleLibraryLoader().Parse(
            """
            schedules:
              broken:
                cron: "0 4 * * *"
                maxConcurrency: -1
            """,
            "schedules.yaml");

        // The schedule still exists and still fires; only the meaningless bound is dropped, and loudly.
        Assert.Equal(ScheduleDefaults.MaxConcurrency, Spec(library, "broken").MaxConcurrency);
        var warning = Assert.Single(library.Warnings);
        Assert.Contains("maxConcurrency", warning, StringComparison.Ordinal);
        Assert.Contains("0 for unbounded", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void InlineSchedule_ReadsTheDeclaredBound()
    {
        // The inline block on a flow must mean exactly what the library entry means.
        var declared = Inline("  maxConcurrency: 2");
        var omitted = Inline(null);
        var unbounded = Inline("  maxConcurrency: 0");

        Assert.Equal(2, declared.MaxConcurrency);
        Assert.Equal(ScheduleDefaults.MaxConcurrency, omitted.MaxConcurrency);
        Assert.Null(unbounded.MaxConcurrency);
    }

    private static ScheduleSpec Spec(ScheduleLibrary library, string name)
        => Assert.Single(library.Schedules, s => s.Name == name).Spec;

    private static ScheduleSpec Inline(string? boundLine)
    {
        var yaml = "flowType: ing\n"
            + "name: some_flow_02_ing\n"
            + "schedule:\n"
            + "  name: nightly\n"
            + "  cron: \"0 4 * * *\"\n"
            + (boundLine is null ? string.Empty : boundLine + "\n")
            + "connections:\n"
            + "  src: \"Server=s;Database=d;Integrated Security=true;\"\n"
            + "  ods: \"Server=s;Database=d;Integrated Security=true;\"\n"
            + "source:\n"
            + "  server: src\n"
            + "  object: \"[db].[dbo].[t]\"\n"
            + "target:\n"
            + "  server: ods\n"
            + "  object: \"[db].[arc].[T]\"\n"
            + "load:\n"
            + "  keyColumns: [id]\n";

        var loader = new YamlDocumentLoader(
            new YamlFlowLoader(), new YamlIngestionFlowLoader(), new YamlExportFlowLoader(),
            new YamlStoredProcedureFlowLoader(), new YamlInvokeFlowLoader(), new YamlHealthCheckFlowLoader(),
            new YamlSourceControlFlowLoader(), new YamlBatchFlowLoader(), new YamlAcquireFlowLoader(),
            new YamlCopyFlowLoader(), new YamlSftpFlowLoader(), new YamlCalendarFlowLoader(), new YamlTranslateFlowLoader());

        var document = loader.Parse(yaml);
        Assert.NotNull(document.Schedule);
        return document.Schedule;
    }
}
