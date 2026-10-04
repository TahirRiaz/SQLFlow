using SqlFlow.Catalog;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// When a sync-now has been answered, and when a sync is in progress, read from a source's columns alone. A sync-now is
/// answered only by an attempt that started at or after it and has recorded an outcome: an attempt already running
/// when the operator asked may have cloned the branch before the commit they synced for.
/// </summary>
public sealed class RepoSyncStateTests
{
    private static readonly DateTime Requested = new(2026, 10, 4, 6, 10, 5, DateTimeKind.Utc);
    private static readonly TimeSpan Abandoned = TimeSpan.FromMinutes(30);

    private static CatalogRepoSource Source(DateTime? requested, DateTime? started, DateTime? finished, bool enabled = true) => new()
    {
        Name = "recall",
        Branch = "main",
        Enabled = enabled,
        SyncRequestedUtc = requested,
        SyncStartedUtc = started,
        LastSyncUtc = finished,
    };

    [Fact]
    public void AnAttemptStartedAfterTheRequest_AndFinished_AnswersIt()
    {
        var source = Source(Requested, Requested.AddSeconds(20), Requested.AddSeconds(23));

        Assert.True(RepoSourceStore.IsSyncAnswered(source, Requested));
        Assert.False(RepoSourceStore.IsSyncRunning(source));
        Assert.False(RepoSourceStore.IsSyncPending(source, Requested.AddSeconds(30), Abandoned));
    }

    [Fact]
    public void AnAttemptAlreadyRunningAtTheRequest_DoesNotAnswerIt_EvenWhenItFinishes()
    {
        // Started a second before the operator asked, finished two seconds after: it may have cloned before the commit.
        var source = Source(Requested, Requested.AddSeconds(-1), Requested.AddSeconds(2));

        Assert.False(RepoSourceStore.IsSyncAnswered(source, Requested));
        Assert.True(RepoSourceStore.IsSyncPending(source, Requested.AddSeconds(3), Abandoned));
    }

    [Fact]
    public void AnAttemptStartedAfterTheRequest_ButStillRunning_DoesNotAnswerIt()
    {
        var source = Source(Requested, Requested.AddSeconds(20), Requested.AddSeconds(-300));

        Assert.False(RepoSourceStore.IsSyncAnswered(source, Requested));
        Assert.True(RepoSourceStore.IsSyncRunning(source));
        Assert.True(RepoSourceStore.IsSyncPending(source, Requested.AddSeconds(21), Abandoned));
    }

    [Fact]
    public void ARunningAttemptNobodyAskedFor_IsPending()
    {
        var started = Requested.AddHours(1);
        var source = Source(requested: null, started, finished: started.AddMinutes(-5));

        Assert.True(RepoSourceStore.IsSyncPending(source, started.AddSeconds(2), Abandoned));
    }

    [Fact]
    public void ARequestNoLoopPickedUp_StopsBeingPending_OnceAbandoned()
    {
        var source = Source(Requested, started: null, finished: Requested.AddDays(-1));

        Assert.True(RepoSourceStore.IsSyncPending(source, Requested.AddMinutes(29), Abandoned));
        Assert.False(RepoSourceStore.IsSyncPending(source, Requested.AddMinutes(31), Abandoned));
    }

    [Fact]
    public void ADisabledSource_IsNeverPending()
    {
        var source = Source(Requested, started: null, finished: null, enabled: false);

        Assert.False(RepoSourceStore.IsSyncPending(source, Requested.AddSeconds(1), Abandoned));
    }
}
