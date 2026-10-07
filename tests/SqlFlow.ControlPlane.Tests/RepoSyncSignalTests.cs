using SqlFlow.ControlPlane.Background;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The wake that starts a sync-now at once instead of on the sync loop's next scan: a wake ends the loop's wait
/// whether it comes before or during it, a wait nobody wakes lasts its interval, and any number of wakes start one scan.
/// </summary>
public sealed class RepoSyncSignalTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task AWakeBeforeTheWait_EndsItAtOnce()
    {
        using var signal = new RepoSyncSignal();
        signal.Wake();

        Assert.True(await signal.WaitAsync(Interval, CancellationToken.None));
    }

    [Fact]
    public async Task AWakeDuringTheWait_EndsIt()
    {
        using var signal = new RepoSyncSignal();
        var waiting = signal.WaitAsync(Interval, CancellationToken.None);
        Assert.False(waiting.IsCompleted);

        signal.Wake();

        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None));
    }

    [Fact]
    public async Task AWaitNobodyWakes_LastsItsInterval()
    {
        using var signal = new RepoSyncSignal();

        Assert.False(await signal.WaitAsync(Short, CancellationToken.None));
    }

    [Fact]
    public async Task ManyWakes_StartOneScan()
    {
        using var signal = new RepoSyncSignal();
        signal.Wake();
        signal.Wake();
        signal.Wake();

        Assert.True(await signal.WaitAsync(Interval, CancellationToken.None));
        Assert.False(await signal.WaitAsync(Short, CancellationToken.None));
    }
}
