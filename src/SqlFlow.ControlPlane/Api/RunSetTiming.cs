using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// When a set of runs ran and how long it took: a run group, or the runs one schedule fire enqueued. The set started
/// when its first member started and ended when its last member reached a terminal state, so its duration is the
/// wall-clock time the set took, not the sum of its members' durations (members of one wave run side by side). Time
/// the set spent waiting before its first member started is <c>StartedUtc - EnqueuedUtc</c>, which a caller holding the
/// enqueue instant can show beside it.
/// </summary>
/// <param name="StartedUtc">When the first member started; null while none has (every member still queued, or the
/// set ended with every member cancelled or skipped before it ran).</param>
/// <param name="EndedUtc">When the last member ended, once every member is terminal; null while any is still queued or
/// running.</param>
/// <param name="DurationSeconds">The seconds from <paramref name="StartedUtc"/> to <paramref name="EndedUtc"/>; null
/// until the set has ended, and for a set none of whose members started.</param>
public sealed record RunSetTiming(DateTime? StartedUtc, DateTime? EndedUtc, double? DurationSeconds)
{
    /// <summary>The timing of a set whose earliest member start is <paramref name="firstStartUtc"/> and latest member end
    /// is <paramref name="lastEndUtc"/>. <paramref name="finished"/> says every member is terminal: until then the latest
    /// end belongs to a member that finished early, not to the set.</summary>
    public static RunSetTiming Of(DateTime? firstStartUtc, DateTime? lastEndUtc, bool finished)
    {
        var endedUtc = finished ? lastEndUtc : null;
        return new RunSetTiming(firstStartUtc, endedUtc, RunElapsed.Seconds(firstStartUtc, endedUtc));
    }
}
