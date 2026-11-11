using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// A repo source's automatic sync, turned on or off per source: what the switch does to the source's schedule, how
/// requests to move it become one, what the configuration may pin, who may change it through the API, and the managed
/// sync of a real git repository holding its commit until asked, moving only forward, and refusing a commit off its branch.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RepoSourceAutoSyncTests
{
    private const string FirstSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static readonly DateTime T0 = new(2026, 11, 7, 9, 0, 0, DateTimeKind.Utc);

    [SkippableFact]
    public async Task Turning_auto_sync_off_cancels_the_periodic_sync_and_a_claim_then_gives_none()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (name, id) = NewSource();

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                await RepoSourceStore.UpsertAsync(db, name, "https://git.invalid/estate.git", "main", enabled: true, syncIntervalSeconds: 3600, T0);
                Assert.True(await RepoSourceStore.TryClaimSyncAsync(db, id, T0, T0.AddHours(1), T0.AddSeconds(1)));
                await RepoSourceStore.RecordSuccessAsync(db, id, FirstSha, T0.AddSeconds(2));
            }

            var synced = await ReadAsync(cs, id);
            Assert.True(synced.AutoSync);
            Assert.Equal(T0.AddHours(1), synced.NextSyncUtc);

            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.Equal(AutoSyncChange.Applied, (await RepoSourceStore.SetAutoSyncAsync(db, id, false, "alice", T0.AddMinutes(1))).Outcome);
                Assert.Equal(AutoSyncChange.Unchanged, (await RepoSourceStore.SetAutoSyncAsync(db, id, false, "bob", T0.AddMinutes(2))).Outcome);
                Assert.Equal(AutoSyncChange.NotFound, (await RepoSourceStore.SetAutoSyncAsync(db, Guid.NewGuid(), false, "alice", T0)).Outcome);
            }

            var off = await ReadAsync(cs, id);
            Assert.False(off.AutoSync);
            Assert.Null(off.NextSyncUtc);
            Assert.Equal("alice", off.AutoSyncUpdatedBy);
            Assert.Equal(T0.AddMinutes(1), off.AutoSyncUpdatedUtc);

            // A recompute the catalog needs makes it due, without asking it to move; the claim then gives it no next sync.
            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.Equal(RepoSourceMutation.Applied, await RepoSourceStore.ResyncAsync(db, id, T0.AddMinutes(3)));
                var due = await ReadAsync(cs, id);
                Assert.Equal(T0.AddMinutes(3), due.NextSyncUtc);
                Assert.Null(due.MoveRequestedUtc);
                Assert.True(due.ForceLineageOnNextSync);
                Assert.True(await RepoSourceStore.TryClaimSyncAsync(db, id, T0.AddMinutes(3), T0.AddMinutes(63), T0.AddMinutes(4)));
            }

            Assert.Null((await ReadAsync(cs, id)).NextSyncUtc);

            // On again, it is due at once to catch up with its branch.
            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.Equal(AutoSyncChange.Applied, (await RepoSourceStore.SetAutoSyncAsync(db, id, true, "alice", T0.AddMinutes(5))).Outcome);
            }

            Assert.Equal(T0.AddMinutes(5), (await ReadAsync(cs, id)).NextSyncUtc);
        }
        finally
        {
            await RemoveAsync(cs, id);
        }
    }

    [SkippableFact]
    public async Task A_source_that_never_synced_keeps_retrying_its_first_sync_with_auto_sync_off()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (name, id) = NewSource();

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                await RepoSourceStore.UpsertAsync(db, name, "https://git.invalid/estate.git", "main", enabled: true, syncIntervalSeconds: 60, T0);
                await RepoSourceStore.SetAutoSyncAsync(db, id, false, "alice", T0);
                Assert.Equal(T0, (await ReadAsync(cs, id)).NextSyncUtc);
                Assert.True(await RepoSourceStore.TryClaimSyncAsync(db, id, T0, T0.AddMinutes(1), T0.AddSeconds(1)));
                await RepoSourceStore.RecordFailureAsync(db, id, "the remote could not be read", T0.AddSeconds(2));
            }

            Assert.Equal(T0.AddMinutes(1), (await ReadAsync(cs, id)).NextSyncUtc);

            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.True(await RepoSourceStore.TryClaimSyncAsync(db, id, T0.AddMinutes(1), T0.AddMinutes(2), T0.AddMinutes(1)));
                await RepoSourceStore.RecordSuccessAsync(db, id, FirstSha, T0.AddMinutes(1).AddSeconds(1));
                Assert.True(await RepoSourceStore.TryClaimSyncAsync(db, id, T0.AddMinutes(2), T0.AddMinutes(3), T0.AddMinutes(2)));
            }

            // The claim made after its first success gives it no next sync: from then on it waits to be asked.
            Assert.Null((await ReadAsync(cs, id)).NextSyncUtc);
        }
        finally
        {
            await RemoveAsync(cs, id);
        }
    }

    [SkippableFact]
    public async Task Moves_asked_for_become_one_and_an_attempt_clears_only_the_move_it_answered()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (name, id) = NewSource();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            await RepoSourceStore.UpsertAsync(db, name, "https://git.invalid/estate.git", "main", enabled: true, syncIntervalSeconds: 3600, T0);
            Assert.True(await RepoSourceStore.TryClaimSyncAsync(db, id, T0, T0.AddHours(1), T0.AddSeconds(1)));
            await RepoSourceStore.RecordSuccessAsync(db, id, FirstSha, T0.AddSeconds(2));
            await RepoSourceStore.SetAutoSyncAsync(db, id, false, "alice", T0.AddSeconds(3));

            // A move to the tip no attempt has taken stays one when a request then names a commit: the tip holds it too.
            await RepoSourceStore.RequestSyncAsync(db, id, T0.AddMinutes(1), forceLineage: false, RepoSyncMove.BranchTip, null);
            await RepoSourceStore.RequestSyncAsync(db, id, T0.AddMinutes(2), forceLineage: false, RepoSyncMove.Commit, "bbbbbbb");
            var tip = await ReadAsync(cs, id);
            Assert.Equal(T0.AddMinutes(2), tip.MoveRequestedUtc);
            Assert.Null(tip.MoveToCommit);

            Assert.True(await RepoSourceStore.TryClaimSyncAsync(db, id, T0.AddMinutes(2), T0.AddHours(2), T0.AddMinutes(3)));
            Assert.True(RepoSourceStore.AnswersMove(await ReadAsync(cs, id)));
            await RepoSourceStore.RecordSuccessAsync(db, id, "cccccccccccccccccccccccccccccccccccccccc", T0.AddMinutes(4));
            Assert.Null((await ReadAsync(cs, id)).MoveRequestedUtc);

            // Of two commits asked for before an attempt, the later request's is kept.
            await RepoSourceStore.RequestSyncAsync(db, id, T0.AddMinutes(5), forceLineage: false, RepoSyncMove.Commit, "ddddddd");
            await RepoSourceStore.RequestSyncAsync(db, id, T0.AddMinutes(6), forceLineage: false, RepoSyncMove.Commit, "eeeeeee");
            Assert.Equal("eeeeeee", (await ReadAsync(cs, id)).MoveToCommit);

            // A move asked for while an attempt runs is the next attempt's: the attempt's failure leaves it, and a
            // recompute asked for meanwhile leaves it too.
            Assert.True(await RepoSourceStore.TryClaimSyncAsync(db, id, T0.AddMinutes(6), T0.AddHours(2), T0.AddMinutes(7)));
            await RepoSourceStore.RequestSyncAsync(db, id, T0.AddMinutes(8), forceLineage: false, RepoSyncMove.Commit, "fffffff");
            await RepoSourceStore.ResyncAsync(db, id, T0.AddMinutes(9));
            await RepoSourceStore.RecordFailureAsync(db, id, "a failure", T0.AddMinutes(10));
            var pending = await ReadAsync(cs, id);
            Assert.Equal(T0.AddMinutes(8), pending.MoveRequestedUtc);
            Assert.Equal("fffffff", pending.MoveToCommit);
            Assert.Equal(T0.AddMinutes(9), pending.NextSyncUtc);

            Assert.True(await RepoSourceStore.TryClaimSyncAsync(db, id, T0.AddMinutes(9), T0.AddHours(3), T0.AddMinutes(11)));
            await RepoSourceStore.RecordFailureAsync(db, id, "a failure that answers the move", T0.AddMinutes(12));
            var answered = await ReadAsync(cs, id);
            Assert.Null(answered.MoveRequestedUtc);
            Assert.Null(answered.MoveToCommit);
            Assert.Null(answered.NextSyncUtc);

            await Assert.ThrowsAsync<ArgumentException>(
                () => RepoSourceStore.RequestSyncAsync(db, id, T0, forceLineage: false, RepoSyncMove.Commit, "main"));
        }
        finally
        {
            await RemoveAsync(cs, id);
        }
    }

    [SkippableFact]
    public async Task A_source_keeps_its_auto_sync_when_registered_again_and_a_new_branch_asks_it_to_move()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (name, id) = NewSource();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            await RepoSourceStore.UpsertAsync(db, name, "https://git.invalid/estate.git", "main", enabled: true, syncIntervalSeconds: 3600, T0);
            Assert.True(await RepoSourceStore.TryClaimSyncAsync(db, id, T0, T0.AddHours(1), T0.AddSeconds(1)));
            await RepoSourceStore.RecordSuccessAsync(db, id, FirstSha, T0.AddSeconds(2));
            await RepoSourceStore.SetAutoSyncAsync(db, id, false, "alice", T0.AddSeconds(3));

            await RepoSourceStore.UpsertAsync(db, name, "https://git.invalid/estate.git", "main", enabled: true, syncIntervalSeconds: 600, T0.AddMinutes(1));
            var same = await ReadAsync(cs, id);
            Assert.False(same.AutoSync);
            Assert.Null(same.MoveRequestedUtc);
            Assert.Equal(T0.AddMinutes(1), same.NextSyncUtc);

            await RepoSourceStore.UpsertAsync(db, name, "https://git.invalid/estate.git", "release", enabled: true, syncIntervalSeconds: 600, T0.AddMinutes(2));
            var moved = await ReadAsync(cs, id);
            Assert.False(moved.AutoSync);
            Assert.Equal(T0.AddMinutes(2), moved.MoveRequestedUtc);
            Assert.Null(moved.MoveToCommit);
            Assert.Equal(T0.AddMinutes(2), moved.NextSyncUtc);
        }
        finally
        {
            await RemoveAsync(cs, id);
        }
    }

    [SkippableFact]
    public async Task The_configuration_pins_auto_sync_until_it_stops_saying_and_the_value_then_stays()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (name, id) = NewSource();
        var setting = BootstrapKey(name);
        ConfiguredRepository Configured(bool? autoSync)
            => new(name, "https://git.invalid/estate.git", null, "main", 300, null, null, [], setting, autoSync);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.Contains(name, (await RepoSourceStore.ApplyConfiguredAsync(db, [Configured(false)], T0)).Registered);
                var refused = await RepoSourceStore.SetAutoSyncAsync(db, id, true, "alice", T0.AddMinutes(1));
                Assert.Equal(AutoSyncChange.SetByConfiguration, refused.Outcome);
                Assert.Equal(setting + ":AutoSync", refused.Setting);
                Assert.Empty((await RepoSourceStore.ApplyConfiguredAsync(db, [Configured(false)], T0.AddMinutes(2))).Updated);
            }

            var pinned = await ReadAsync(cs, id);
            Assert.False(pinned.AutoSync);
            Assert.Equal(setting + ":AutoSync", pinned.AutoSyncConfiguredBy);

            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.Contains(name, (await RepoSourceStore.ApplyConfiguredAsync(db, [Configured(null)], T0.AddMinutes(3))).Updated);
            }

            var unpinned = await ReadAsync(cs, id);
            Assert.False(unpinned.AutoSync);
            Assert.Null(unpinned.AutoSyncConfiguredBy);

            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.Equal(AutoSyncChange.Applied, (await RepoSourceStore.SetAutoSyncAsync(db, id, true, "alice", T0.AddMinutes(4))).Outcome);
                Assert.Contains(name, (await RepoSourceStore.ApplyConfiguredAsync(db, [Configured(false)], T0.AddMinutes(5))).Updated);
                Assert.False((await ReadAsync(cs, id)).AutoSync);

                // Released, it is an ordinary source whose automatic sync is an administrator's again.
                Assert.Contains(name, (await RepoSourceStore.ApplyConfiguredAsync(db, [], T0.AddMinutes(6))).Released);
            }

            var released = await ReadAsync(cs, id);
            Assert.Null(released.ConfiguredBy);
            Assert.Null(released.AutoSyncConfiguredBy);
            Assert.False(released.AutoSync);
        }
        finally
        {
            await RemoveAsync(cs, id);
        }
    }

    [SkippableFact]
    public async Task Only_an_administrator_turns_auto_sync_on_or_off_and_not_one_the_configuration_sets()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (name, id) = NewSource();
        var (pinnedName, pinnedId) = NewSource();

        // The pinned source is the host's own configuration, written by its start-up pass: the binding of AutoSync too.
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs)
            .WithSetting("ControlPlane:ManagedSync:Enabled", "false")
            .WithSetting("ControlPlane:Worker:Enabled", "false")
            .WithSetting($"{BootstrapKey(pinnedName)}:RemoteUrl", "https://git.invalid/pinned.git")
            .WithSetting($"{BootstrapKey(pinnedName)}:AutoSync", "false");
        using var client = factory.CreateClient();

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                await RepoSourceStore.UpsertAsync(db, name, "https://git.invalid/estate.git", "main", enabled: true, syncIntervalSeconds: 300, T0);
            }

            for (var attempt = 0; attempt < 120; attempt++)
            {
                await using var db = CatalogDatabase.Create(cs);
                if (await db.RepoSources.AsNoTracking().AnyAsync(s => s.Id == pinnedId && s.AutoSyncConfiguredBy != null))
                {
                    break;
                }

                await Task.Delay(250);
            }

            var pinned = await ReadAsync(cs, pinnedId);
            Assert.False(pinned.AutoSync);
            Assert.Equal(BootstrapKey(pinnedName) + ":AutoSync", pinned.AutoSyncConfiguredBy);

            var operate = await AiContextTestKit.IssueTokenAsync(client, ["read", "operate"]);
            var admin = await AiContextTestKit.IssueTokenAsync(client, ["read", "operate", "admin"]);

            using (var refused = await TriggerEstate.Send(client, operate, HttpMethod.Put, $"/api/v1/repos/sources/{id:D}/auto-sync", new SetAutoSyncRequest(false)))
            {
                Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            }

            using (var missing = await TriggerEstate.Send(client, admin, HttpMethod.Put, $"/api/v1/repos/sources/{Guid.NewGuid():D}/auto-sync", new SetAutoSyncRequest(false)))
            {
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            }

            using (var empty = await TriggerEstate.Send(client, admin, HttpMethod.Put, $"/api/v1/repos/sources/{id:D}/auto-sync", new SetAutoSyncRequest(null)))
            {
                Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
            }

            using (var configured = await TriggerEstate.Send(client, admin, HttpMethod.Put, $"/api/v1/repos/sources/{pinnedId:D}/auto-sync", new SetAutoSyncRequest(true)))
            {
                Assert.Equal(HttpStatusCode.Conflict, configured.StatusCode);
                Assert.Contains(BootstrapKey(pinnedName) + ":AutoSync", await configured.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            using var applied = await TriggerEstate.Send(client, admin, HttpMethod.Put, $"/api/v1/repos/sources/{id:D}/auto-sync", new SetAutoSyncRequest(false));
            Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
            var source = await applied.Content.ReadFromJsonAsync<RepoSourceDto>();
            Assert.NotNull(source);
            Assert.False(source.AutoSync);
            Assert.Null(source.AutoSyncConfiguredBy);
            Assert.False(string.IsNullOrWhiteSpace(source.AutoSyncUpdatedBy));
            Assert.NotNull(source.AutoSyncUpdatedUtc);

            var listed = await TriggerEstate.Get<PagedResult<RepoSourceDto>>(client, operate, "/api/v1/repos/sources?pageSize=200");
            Assert.False(listed.Items.Single(s => s.Id == id).AutoSync);
            Assert.Equal(BootstrapKey(pinnedName) + ":AutoSync", listed.Items.Single(s => s.Id == pinnedId).AutoSyncConfiguredBy);
        }
        finally
        {
            await RemoveAsync(cs, id);
            await RemoveAsync(cs, pinnedId);
        }
    }

    /// <summary>
    /// The managed sync of a real repository with automatic sync off: a recompute reads the commit it holds while the
    /// branch moves on; a move to a commit reads exactly that commit; a move back stays put; a commit off the branch is
    /// refused with the source left where it was; and a sync-now moves it to the tip.
    /// </summary>
    [SkippableFact]
    public async Task A_repository_that_syncs_only_on_request_holds_its_commit_until_asked_and_only_moves_forward()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs).WithSetting("ControlPlane:Worker:Enabled", "false");
        using var client = factory.CreateClient();
        var admin = await AiContextTestKit.IssueTokenAsync(client, ["read", "operate", "admin"]);
        await using var estate = TriggerEstate.Seed(cs, "src_gated", [new("land", "nightly")]);
        var first = (await estate.RegisterAsync(client, admin)).LastSyncedSha!;

        using (var off = await TriggerEstate.Send(client, admin, HttpMethod.Put, $"/api/v1/repos/sources/{estate.SourceId:D}/auto-sync", new SetAutoSyncRequest(false)))
        {
            Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        }

        var second = estate.Commit("data/a.csv", "id\n1\n");
        var third = estate.Commit("data/b.csv", "id\n2\n");
        var side = estate.CommitOnSideBranch("feature/unmerged", "data/c.csv", "id\n3\n");

        var held = await AskAndWaitAsync(cs, estate.SourceId, db => RepoSourceStore.ResyncAsync(db, estate.SourceId, DateTime.UtcNow));
        Assert.Equal(first, held.LastSyncedSha);
        Assert.Null(held.LastError);
        Assert.Contains(await LinesAsync(cs, held), e => e.Step == "target" && e.Message.Contains("syncing the commit it holds", StringComparison.Ordinal));
        Assert.Contains(await LinesAsync(cs, held), e => e.Step == "target" && e.Message.Contains($"The tip of {estate.Branch} is", StringComparison.Ordinal));

        var moved = await AskAndWaitAsync(cs, estate.SourceId, db => RepoSourceStore.RequestSyncAsync(
            db, estate.SourceId, DateTime.UtcNow, forceLineage: false, RepoSyncMove.Commit, second[..10]));
        Assert.Equal(second, moved.LastSyncedSha);
        Assert.Null(moved.LastError);

        var stayed = await AskAndWaitAsync(cs, estate.SourceId, db => RepoSourceStore.RequestSyncAsync(
            db, estate.SourceId, DateTime.UtcNow, forceLineage: false, RepoSyncMove.Commit, first));
        Assert.Equal(second, stayed.LastSyncedSha);
        Assert.Null(stayed.LastError);
        Assert.Contains(await LinesAsync(cs, stayed), e => e.Step == "target" && e.Message.Contains("since a sync never moves back", StringComparison.Ordinal));

        var refused = await AskAndWaitAsync(cs, estate.SourceId, db => RepoSourceStore.RequestSyncAsync(
            db, estate.SourceId, DateTime.UtcNow, forceLineage: false, RepoSyncMove.Commit, side));
        Assert.Equal(second, refused.LastSyncedSha);
        Assert.Contains("is not on branch", refused.LastError, StringComparison.Ordinal);

        using var now = await TriggerEstate.Send(client, admin, HttpMethod.Post, $"/api/v1/repos/sources/{estate.SourceId:D}/sync", new { });
        var tip = await now.Content.ReadFromJsonAsync<RepoSourceDto>();
        Assert.Equal(HttpStatusCode.OK, now.StatusCode);
        Assert.Equal(third, tip!.LastSyncedSha);
        Assert.False(tip.AutoSync);

        // The sync-now's queued line, written before its attempt began, names who asked.
        var queued = await estate.WithCatalogAsync(db => db.ActivityEvents.AsNoTracking()
            .Where(e => e.Kind == ActivityKinds.RepoSync && e.SubjectKey == estate.SourceId.ToString() && e.Step == "queued")
            .OrderByDescending(e => e.Id).Select(e => e.Message).FirstAsync());
        Assert.StartsWith("Sync requested by ", queued, StringComparison.Ordinal);
    }

    /// <summary>The trace lines of the attempt that recorded <paramref name="source"/>'s last outcome.</summary>
    private static async Task<IReadOnlyList<CatalogActivityEvent>> LinesAsync(string cs, CatalogRepoSource source)
    {
        await using var db = CatalogDatabase.Create(cs);
        return await db.ActivityEvents.AsNoTracking()
            .Where(e => e.Kind == ActivityKinds.RepoSync && e.ActivityId == source.LastSyncActivityId)
            .OrderBy(e => e.Id).ToListAsync();
    }

    /// <summary>Makes a request at the current time, then waits for the attempt that answers it and returns the source.</summary>
    private static async Task<CatalogRepoSource> AskAndWaitAsync(string cs, Guid id, Func<CatalogDbContext, Task<RepoSourceMutation>> ask)
    {
        DateTime asked;
        await using (var db = CatalogDatabase.Create(cs))
        {
            asked = DateTime.UtcNow;
            Assert.Equal(RepoSourceMutation.Applied, await ask(db));
            asked = (await db.RepoSources.AsNoTracking().SingleAsync(s => s.Id == id)).SyncRequestedUtc ?? asked;
        }

        for (var attempt = 0; attempt < 240; attempt++)
        {
            var source = await ReadAsync(cs, id);
            if (RepoSourceStore.IsSyncAnswered(source, asked))
            {
                return source;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"No sync of repo source {id} answered the request made at {asked:O}.");
    }

    private static (string Name, Guid Id) NewSource()
    {
        var name = "src_auto_" + Guid.NewGuid().ToString("N")[..8];
        return (name, FlowIdentity.FromName($"reposource/{name}"));
    }

    private static string BootstrapKey(string name) => $"ControlPlane:Bootstrap:Repositories:{name}";

    private static async Task<CatalogRepoSource> ReadAsync(string cs, Guid id)
    {
        await using var db = CatalogDatabase.Create(cs);
        return await db.RepoSources.AsNoTracking().SingleAsync(s => s.Id == id);
    }

    private static async Task RemoveAsync(string cs, Guid id)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.ActivityEvents.Where(e => e.SubjectKey == id.ToString()).ExecuteDeleteAsync();
        await db.RepoSources.Where(s => s.Id == id).ExecuteDeleteAsync();
    }
}
