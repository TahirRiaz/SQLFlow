using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The arithmetic behind how long a set of runs and a pipeline's runs take, without a database: a set's span runs from
/// its first start to its last end and holds no end while a member is still executing, and a pipeline's typical figures
/// are the median, mean and extremes of its durations.
/// </summary>
public sealed class RunTimingTests
{
    private static readonly DateTime Anchor = new(2026, 6, 19, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ASetThatFinished_TookFromItsFirstStartToItsLastEnd()
    {
        var timing = RunSetTiming.Of(Anchor, Anchor.AddMinutes(9), finished: true);

        Assert.Equal(Anchor, timing.StartedUtc);
        Assert.Equal(Anchor.AddMinutes(9), timing.EndedUtc);
        Assert.Equal(540, timing.DurationSeconds);
    }

    [Fact]
    public void ASetStillExecuting_HasStartedButNotEnded()
    {
        // The latest end belongs to a member that finished early, not to the set.
        var timing = RunSetTiming.Of(Anchor, Anchor.AddMinutes(2), finished: false);

        Assert.Equal(Anchor, timing.StartedUtc);
        Assert.Null(timing.EndedUtc);
        Assert.Null(timing.DurationSeconds);
    }

    [Fact]
    public void ASetNoMemberOfWhichStarted_EndedWithoutADuration()
    {
        var timing = RunSetTiming.Of(null, Anchor, finished: true);

        Assert.Null(timing.StartedUtc);
        Assert.Equal(Anchor, timing.EndedUtc);
        Assert.Null(timing.DurationSeconds);
    }

    [Fact]
    public void APipelineProfile_IsTheMedianMeanAndExtremesOfItsDurations()
    {
        var last = new PipelineRunTimingDto(Guid.NewGuid(), RunStatuses.Failed, Anchor, Anchor.AddSeconds(5), 5);

        var odd = PipelineRunDurations.Profile([30, 10, 20], last, running: null);
        Assert.Equal((3, 20.0, 20.0, 10.0, 30.0), (odd.Runs, odd.MedianSeconds!.Value, odd.AverageSeconds!.Value, odd.FastestSeconds!.Value, odd.SlowestSeconds!.Value));
        Assert.Same(last, odd.Last);
        Assert.Null(odd.Running);

        // An even count's median sits between its two middle values, and an outlier moves the mean but not the median.
        var even = PipelineRunDurations.Profile([100, 10, 30, 20], last: null, running: null);
        Assert.Equal(25, even.MedianSeconds);
        Assert.Equal(40, even.AverageSeconds);
    }

    [Fact]
    public void APipelineWithoutTimedSucceededRuns_HasNoTypicalFigures()
    {
        var running = new PipelineRunTimingDto(Guid.NewGuid(), RunStatuses.Running, Anchor, null, null);

        var profile = PipelineRunDurations.Profile([], last: null, running);

        Assert.Equal(0, profile.Runs);
        Assert.Null(profile.MedianSeconds);
        Assert.Null(profile.AverageSeconds);
        Assert.Null(profile.FastestSeconds);
        Assert.Null(profile.SlowestSeconds);
        Assert.Same(running, profile.Running);
    }
}
