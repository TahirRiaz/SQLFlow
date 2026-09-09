using SqlFlow.Catalog;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The schedule clock: cron and interval next-fire computation and validation. Pure and deterministic (no database,
/// no wall clock), so these run everywhere and pin the timing semantics the scheduler depends on.
/// </summary>
public sealed class ScheduleClockTests
{
    [Fact]
    public void NextFire_Interval_IsNowPlusInterval()
    {
        var from = new DateTime(2026, 6, 19, 8, 0, 0, DateTimeKind.Utc);

        var next = ScheduleClock.NextFire(cron: null, intervalSeconds: 300, "UTC", from);

        Assert.Equal(from.AddSeconds(300), next);
    }

    [Fact]
    public void NextFire_DailyCronUtc_IsTheNextSixAm()
    {
        // "0 6 * * *" = 06:00 every day, evaluated in UTC.
        var beforeSix = new DateTime(2026, 6, 19, 5, 0, 0, DateTimeKind.Utc);
        var afterSix = new DateTime(2026, 6, 19, 7, 0, 0, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2026, 6, 19, 6, 0, 0, DateTimeKind.Utc), ScheduleClock.NextFire("0 6 * * *", null, "UTC", beforeSix));
        Assert.Equal(new DateTime(2026, 6, 20, 6, 0, 0, DateTimeKind.Utc), ScheduleClock.NextFire("0 6 * * *", null, "UTC", afterSix));
    }

    [Fact]
    public void NextFire_SixFieldCron_UsesSecondsGranularity()
    {
        // Six fields: a leading seconds field. "30 * * * * *" = at second 30 of every minute.
        var from = new DateTime(2026, 6, 19, 8, 0, 10, DateTimeKind.Utc);

        var next = ScheduleClock.NextFire("30 * * * * *", null, "UTC", from);

        Assert.Equal(new DateTime(2026, 6, 19, 8, 0, 30, DateTimeKind.Utc), next);
    }

    [Fact]
    public void TryValidate_RejectsSettingBothCronAndInterval()
    {
        Assert.False(ScheduleClock.TryValidate("0 6 * * *", 300, "UTC", out var error));
        Assert.Contains("exactly one", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidate_RejectsSettingNeither()
    {
        Assert.False(ScheduleClock.TryValidate(null, null, "UTC", out var error));
        Assert.Contains("exactly one", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidate_RejectsAMalformedCron()
    {
        Assert.False(ScheduleClock.TryValidate("not a cron", null, "UTC", out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryValidate_RejectsAnUnknownTimezone()
    {
        Assert.False(ScheduleClock.TryValidate("0 6 * * *", null, "Mars/Olympus", out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryValidate_AcceptsAValidCronAndIanaTimezone()
    {
        // IANA ids resolve cross-platform on .NET; this also proves the time-zone database is reachable.
        Assert.True(ScheduleClock.TryValidate("0 6 * * *", null, "Europe/Oslo", out var error), error);
        Assert.Null(error);
    }

    [Fact]
    public void TryValidate_AcceptsAnInterval()
    {
        Assert.True(ScheduleClock.TryValidate(null, 60, "UTC", out var error), error);
        Assert.Null(error);
    }
}
