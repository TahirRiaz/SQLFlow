using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Core.Tests;

/// <summary>
/// Parsing of chained (shadow) schedules in a <c>schedules.yaml</c>: <c>after:</c> declares that a schedule is driven
/// by another's completion instead of by the clock. These pin the contradictory and degenerate cases, which is where
/// a mis-declared chain would otherwise turn into a schedule that fires twice or never.
/// </summary>
public sealed class ScheduleLibraryChainingTests
{
    [Fact]
    public void After_MakesTheScheduleChained_WithNoCadenceOfItsOwn()
    {
        var lib = new YamlScheduleLibraryLoader().Parse("""
            schedules:
              apc_dalane_daily:
                cron: "10 20 * * *"
                timezone: UTC
              apc_haugalandet_daily:
                after: apc_dalane_daily
            """);

        var parent = Assert.Single(lib.Schedules, s => s.Name == "apc_dalane_daily").Spec;
        Assert.Equal("10 20 * * *", parent.Cron);
        Assert.False(parent.IsChained);

        var child = Assert.Single(lib.Schedules, s => s.Name == "apc_haugalandet_daily").Spec;
        Assert.True(child.IsChained);
        Assert.Equal(["apc_dalane_daily"], child.After);
        Assert.Null(child.Cron);
        Assert.Null(child.IntervalSeconds);
    }

    [Fact]
    public void After_WithACron_DropsTheCron_SoItCannotFireTwicePerCycle()
    {
        var lib = new YamlScheduleLibraryLoader().Parse("""
            schedules:
              b:
                after: a
                cron: "0 5 * * *"
            """);

        var spec = Assert.Single(lib.Schedules).Spec;
        Assert.True(spec.IsChained);
        Assert.Null(spec.Cron);
        Assert.Contains(lib.Warnings, w => w.Contains("has no cadence of its own", StringComparison.Ordinal));
    }

    [Fact]
    public void After_PointingAtItself_IsDropped()
    {
        var lib = new YamlScheduleLibraryLoader().Parse("""
            schedules:
              a:
                after: a
            """);

        Assert.Empty(lib.Schedules);
        Assert.Contains(lib.Warnings, w => w.Contains("chains after itself", StringComparison.Ordinal));
    }

    [Fact]
    public void NeitherCronNorIntervalNorAfter_IsStillDropped()
    {
        var lib = new YamlScheduleLibraryLoader().Parse("""
            schedules:
              a:
                timezone: UTC
            """);

        Assert.Empty(lib.Schedules);
        Assert.Contains(lib.Warnings, w => w.Contains("nor an after", StringComparison.Ordinal));
    }

    [Fact]
    public void ADisabledChainedSchedule_StillParses_SoACutoverCanEnableItLater()
    {
        var lib = new YamlScheduleLibraryLoader().Parse("""
            schedules:
              b:
                after: a
                enabled: false
            """);

        var spec = Assert.Single(lib.Schedules).Spec;
        Assert.True(spec.IsChained);
        Assert.False(spec.Enabled);
    }

    [Fact]
    public void After_AcceptsASequence_ForAStepThatWaitsOnSeveralSources()
    {
        var lib = new YamlScheduleLibraryLoader().Parse("""
            schedules:
              ferry_rebuild:
                after: [apc_daily, norled_daily, mpc_daily]
                parentFreshnessHours: 36
            """);

        var spec = Assert.Single(lib.Schedules, s => s.Name == "ferry_rebuild").Spec;
        Assert.True(spec.IsChained);
        Assert.Equal(["apc_daily", "norled_daily", "mpc_daily"], spec.After);
        Assert.Equal(36, spec.ParentFreshnessHours);
    }

    [Fact]
    public void After_CollapsesRepeats_SoTheFanInCountCanBeSatisfied()
    {
        // A name listed twice would be counted twice by the readiness rule and the schedule could never become ready.
        var lib = new YamlScheduleLibraryLoader().Parse("""
            schedules:
              c:
                after: [a, A, b, "  a  "]
            """);

        var spec = Assert.Single(lib.Schedules, s => s.Name == "c").Spec;
        Assert.Equal(["a", "b"], spec.After);
        Assert.Equal(24, spec.ParentFreshnessHours);
    }

    [Fact]
    public void After_ListingItself_IsIgnored()
    {
        var lib = new YamlScheduleLibraryLoader().Parse("""
            schedules:
              c:
                after: [a, c]
            """);

        Assert.Empty(lib.Schedules);
        Assert.Contains(lib.Warnings, w => w.Contains("chains after itself", StringComparison.Ordinal));
    }
}
