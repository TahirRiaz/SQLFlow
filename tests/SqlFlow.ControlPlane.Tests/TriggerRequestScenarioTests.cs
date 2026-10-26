using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Trigger requests end to end, against a real git repository, the host's own managed sync and its own processor: a merge
/// reported by commit, synced, its schedules fired, its runs followed to their end; and every way a request can go
/// otherwise (the branch moved on, a commit the branch does not hold, a schedule that is missing, paused or disabled, a
/// failed or silent sync, a failed run, a cancel, a retried call, a takeover after a crash). The in-process worker is off,
/// so each test decides how its runs end. An on-demand suite: <c>bash tools/test.sh --suite ExternalTriggers</c>.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Suite", "ExternalTriggers")]
public sealed class TriggerRequestScenarioTests
{
    private static ControlPlaneAppFactory Host(string cs)
        => new ControlPlaneAppFactory().WithCatalog(cs)
            .WithSetting("ControlPlane:Worker:Enabled", "false")
            .WithSetting("ControlPlane:ExternalTriggers:PollSeconds", "1")
            .WithSetting("ControlPlane:Notifications:GuiBaseUrl", "https://gui.example.com");

    private static async Task<(string Cs, ControlPlaneAppFactory Factory, HttpClient Client, string Admin)> StartAsync()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var factory = Host(cs);
        var client = factory.CreateClient();
        var admin = await AiContextTestKit.IssueTokenAsync(client, ["read", "operate", "admin"]);
        return (cs, factory, client, admin);
    }

    [SkippableFact]
    public async Task A_merge_is_synced_its_schedule_fired_and_its_runs_followed_to_success()
    {
        var (cs, factory, client, admin) = await StartAsync();
        await using var _ = factory;
        using var __ = client;
        await using var estate = TriggerEstate.Seed(cs, "trg_happy", [new("land", "nightly"), new("load", "nightly"), new("other", "hourly")]);
        await estate.RegisterAsync(client, admin);
        var trigger = await estate.CreateTriggerAsync(client, admin, "deliver-" + estate.RepoName[^8..], ["nightly"]);
        var token = await TriggerEstate.ExchangeKeyAsync(client, trigger.Key!);
        var merged = estate.Commit("data/land.csv", "id\n1\n");

        var (status, request, body) = await TriggerEstate.RequestAsync(client, token, new CreateTriggerRequest(Commit: merged, Schedules: ["nightly"], RequestKey: "run-1"));

        Assert.True(status == HttpStatusCode.Accepted, body);
        var running = await TriggerEstate.WaitAsync(client, token, request!.Id, d => d.Request.State == TriggerRequestStates.Running);
        Assert.Equal(TriggerSyncOutcomes.Succeeded, running.Request.SyncOutcome);
        Assert.Equal(merged, running.Request.SyncedCommit);
        var nightly = Assert.Single(running.Request.Schedules);
        Assert.Equal(2, nightly.MemberCount);
        Assert.Equal(merged, nightly.CommitSha);
        Assert.Equal(2, running.Runs.Count);
        Assert.All(await estate.RunsAsync(nightly), r => Assert.Equal($"trigger:{trigger.Trigger.Name}", r.RequestedBy));

        await estate.EndRunsAsync(nightly, RunStatuses.Succeeded);
        var done = await TriggerEstate.WaitForEndAsync(client, token, request.Id);

        Assert.Equal(TriggerRequestStates.Succeeded, done.Request.State);
        Assert.Equal("success", done.Request.Conclusion);
        Assert.Equal(TriggerScheduleStates.Succeeded, done.Request.Schedules[0].State);
        Assert.Equal(2, done.Request.Schedules[0].Counts.Succeeded);
        Assert.All(done.Runs, r => Assert.Equal("10 rows loaded", r.Outcome));
        Assert.StartsWith("https://gui.example.com/runs/", done.Runs[0].Url, StringComparison.Ordinal);

        var trace = await estate.TraceAsync(request.Id);
        Assert.Contains(trace, e => e.Step == "request" && e.Message.Contains("asked for a sync", StringComparison.Ordinal));
        Assert.Contains(trace, e => e.Step == "sync:clone" && e.Message.StartsWith("Checked out", StringComparison.Ordinal));
        Assert.Contains(trace, e => e.Step == "fire" && e.Message.Contains("Fired schedule 'nightly'", StringComparison.Ordinal));
        Assert.Equal(2, trace.Count(e => e.Step == "run" && e.Message.Contains("succeeded", StringComparison.Ordinal)));
        Assert.True(trace[^1].Terminal);
        Assert.Equal(ActivityStatuses.Succeeded, trace[^1].Status);

        using var summary = await TriggerEstate.Send(client, token, HttpMethod.Get, $"/api/v1/trigger-requests/{request.Id:D}/summary");
        var markdown = await summary.Content.ReadAsStringAsync();
        Assert.Equal("text/markdown", summary.Content.Headers.ContentType?.MediaType);
        Assert.Contains("synced, and its schedule succeeded", markdown, StringComparison.Ordinal);
        Assert.Contains("Sync trace (", markdown, StringComparison.Ordinal);

        var used = await TriggerEstate.Get<ExternalTriggerDto>(client, admin, $"/api/v1/external-triggers/{trigger.Trigger.Id:D}");
        Assert.NotNull(used.LastUsedUtc);
        Assert.Equal(request.Id, used.LastRequest?.Id);
        Assert.Equal("success", used.LastRequest?.Conclusion);
    }

    [SkippableFact]
    public async Task A_commit_already_synced_needs_no_sync()
    {
        var (cs, factory, client, admin) = await StartAsync();
        await using var _ = factory;
        using var __ = client;
        await using var estate = TriggerEstate.Seed(cs, "trg_current", [new("land", "nightly")]);
        var source = await estate.RegisterAsync(client, admin);

        var (_, request, body) = await TriggerEstate.RequestAsync(client, admin, new CreateTriggerRequest(estate.RepoName, Commit: source.LastSyncedSha![..10]));

        var done = await TriggerEstate.WaitForEndAsync(client, admin, request!.Id);
        Assert.Equal(TriggerRequestStates.Succeeded, done.Request.State);
        Assert.Equal(TriggerSyncOutcomes.Current, done.Request.SyncOutcome);
        Assert.Null(done.Request.SyncRequestedUtc);
        Assert.Null(done.Request.TriggerId);
        Assert.Equal("bootstrap", done.Request.RequestedBy);
    }

    [SkippableFact]
    public async Task A_branch_that_moved_on_past_the_merge_still_contains_it_and_fires()
    {
        var (cs, factory, client, admin) = await StartAsync();
        await using var _ = factory;
        using var __ = client;
        await using var estate = TriggerEstate.Seed(cs, "trg_moved", [new("land", "nightly")]);
        await estate.RegisterAsync(client, admin);
        var merged = estate.Commit("data/a.csv", "id\n1\n");
        var later = estate.Commit("data/b.csv", "id\n2\n");

        var (_, request, _) = await TriggerEstate.RequestAsync(client, admin, new CreateTriggerRequest(estate.RepoName, Commit: merged, Schedules: ["nightly"]));

        var running = await TriggerEstate.WaitAsync(client, admin, request!.Id, d => d.Request.State == TriggerRequestStates.Running);
        Assert.Equal(later, running.Request.SyncedCommit);
        Assert.Equal(later, running.Request.Schedules[0].CommitSha);
        var trace = await estate.TraceAsync(request.Id);
        Assert.Contains(trace, e => e.Message.Contains("which contains", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task A_commit_the_tracked_branch_does_not_hold_fires_nothing_and_says_why()
    {
        var (cs, factory, client, admin) = await StartAsync();
        await using var _ = factory;
        using var __ = client;
        await using var estate = TriggerEstate.Seed(cs, "trg_side", [new("land", "nightly")]);
        await estate.RegisterAsync(client, admin);
        var side = estate.CommitOnSideBranch("feature", "data/x.csv", "id\n1\n");
        estate.Commit("data/y.csv", "id\n2\n");

        var (_, request, _) = await TriggerEstate.RequestAsync(client, admin, new CreateTriggerRequest(estate.RepoName, Commit: side, Schedules: ["nightly"]));

        var done = await TriggerEstate.WaitForEndAsync(client, admin, request!.Id);
        Assert.Equal(TriggerRequestStates.Failed, done.Request.State);
        Assert.Contains("does not contain", done.Request.Detail, StringComparison.Ordinal);
        Assert.Contains($"branch '{estate.Branch}'", done.Request.Detail, StringComparison.Ordinal);
        Assert.Equal(TriggerScheduleStates.NotFired, done.Request.Schedules[0].State);
        Assert.Empty(done.Runs);
    }

    [SkippableFact]
    public async Task A_commit_no_branch_holds_fires_nothing_and_says_it_is_not_in_the_repository()
    {
        var (cs, factory, client, admin) = await StartAsync();
        await using var _ = factory;
        using var __ = client;
        await using var estate = TriggerEstate.Seed(cs, "trg_unknown", [new("land", "nightly")]);
        await estate.RegisterAsync(client, admin);
        estate.Commit("data/y.csv", "id\n2\n");

        var (_, request, _) = await TriggerEstate.RequestAsync(client, admin, new CreateTriggerRequest(estate.RepoName, Commit: new string('a', 40), Schedules: ["nightly"]));

        var done = await TriggerEstate.WaitForEndAsync(client, admin, request!.Id);
        Assert.Equal(TriggerRequestStates.Failed, done.Request.State);
        Assert.Contains("is not in", done.Request.Detail, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Missing_paused_and_disabled_schedules_do_not_fire_and_each_says_why()
    {
        var (cs, factory, client, admin) = await StartAsync();
        await using var _ = factory;
        using var __ = client;
        await using var estate = TriggerEstate.Seed(
            cs, "trg_refuse", [new("land", "nightly"), new("load", "hourly"), new("spare", "weekly")],
            "off: { cron: \"0 0 1 1 *\", enabled: false }");
        await estate.RegisterAsync(client, admin);
        var hourly = await estate.WithCatalogAsync(db => db.Schedules.AsNoTracking().SingleAsync(s => s.RepoId == estate.RepoId && s.Name == "hourly"));
        using (var pause = await TriggerEstate.Send(client, admin, HttpMethod.Post, $"/api/v1/schedules/{hourly.Id:D}/pause"))
        {
            Assert.Equal(HttpStatusCode.OK, pause.StatusCode);
        }

        var (_, request, _) = await TriggerEstate.RequestAsync(client, admin, new CreateTriggerRequest(
            estate.RepoName, Sync: false, Schedules: ["nightly", "hourly", "off", "nope"]));

        var running = await TriggerEstate.WaitAsync(client, admin, request!.Id, d => d.Request.State == TriggerRequestStates.Running);
        var byName = running.Request.Schedules.ToDictionary(s => s.Name);
        Assert.Equal(TriggerSyncOutcomes.Skipped, running.Request.SyncOutcome);
        Assert.Equal(TriggerScheduleStates.Running, byName["nightly"].State);
        Assert.Contains("paused by an operator", byName["hourly"].Detail, StringComparison.Ordinal);
        Assert.Contains("disabled in its YAML", byName["off"].Detail, StringComparison.Ordinal);
        Assert.Contains("No schedule named 'nope'", byName["nope"].Detail, StringComparison.Ordinal);
        Assert.Contains("its schedules are", byName["nope"].Detail, StringComparison.Ordinal);

        await estate.EndRunsAsync(byName["nightly"], RunStatuses.Succeeded);
        var done = await TriggerEstate.WaitForEndAsync(client, admin, request.Id);
        Assert.Equal(TriggerRequestStates.Failed, done.Request.State);
        Assert.Contains("did not fire", done.Request.Detail, StringComparison.Ordinal);
        Assert.Contains("1 succeeded", done.Request.Detail, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task A_request_with_no_schedule_only_syncs()
    {
        var (cs, factory, client, admin) = await StartAsync();
        await using var _ = factory;
        using var __ = client;
        await using var estate = TriggerEstate.Seed(cs, "trg_sync", [new("land", "nightly")]);
        await estate.RegisterAsync(client, admin);
        var merged = estate.Commit("data/a.csv", "id\n1\n");

        var (_, request, _) = await TriggerEstate.RequestAsync(client, admin, new CreateTriggerRequest(estate.RepoName, Commit: merged));

        var done = await TriggerEstate.WaitForEndAsync(client, admin, request!.Id);
        Assert.Equal(TriggerRequestStates.Succeeded, done.Request.State);
        Assert.Equal(merged, done.Request.SyncedCommit);
        Assert.Contains("no schedule was asked for", done.Request.Detail, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task A_sync_that_fails_fails_the_request_with_its_error_and_fires_nothing()
    {
        var (cs, factory, client, admin) = await StartAsync();
        await using var _ = factory;
        using var __ = client;
        await using var estate = TriggerEstate.Seed(cs, "trg_broken", [new("land", "nightly")]);
        await estate.RegisterAsync(client, admin);
        // The remote disappears: the next sync cannot read it.
        var gone = Path.Combine(Path.GetTempPath(), "sqlflow_missing_" + Guid.NewGuid().ToString("N"));
        await estate.WithCatalogAsync(db => db.RepoSources.Where(s => s.Id == estate.SourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RemoteUrl, gone)));

        var (_, request, _) = await TriggerEstate.RequestAsync(client, admin, new CreateTriggerRequest(estate.RepoName, Schedules: ["nightly"]));

        var done = await TriggerEstate.WaitForEndAsync(client, admin, request!.Id);
        Assert.Equal(TriggerRequestStates.Failed, done.Request.State);
        Assert.Equal(TriggerSyncOutcomes.Failed, done.Request.SyncOutcome);
        Assert.False(string.IsNullOrWhiteSpace(done.Request.SyncError));
        Assert.StartsWith("The sync of", done.Request.Detail, StringComparison.Ordinal);
        Assert.Equal(TriggerScheduleStates.NotFired, done.Request.Schedules[0].State);
        var trace = await estate.TraceAsync(request.Id);
        Assert.Contains(trace, e => e.Step!.StartsWith("sync:", StringComparison.Ordinal) && e.Level == "error");
    }

    [SkippableFact]
    public async Task A_sync_nothing_answers_times_out()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        // A host that runs no managed sync, as when every replica opted out: the request waits, then gives up.
        await using var factory = Host(cs).WithSetting("ControlPlane:ManagedSync:Enabled", "false");
        using var client = factory.CreateClient();
        var admin = await AiContextTestKit.IssueTokenAsync(client, ["read", "operate", "admin"]);
        await using var estate = TriggerEstate.Seed(cs, "trg_silent", [new("land", "nightly")]);
        await estate.WithCatalogAsync(db => RepoSourceStore.UpsertAsync(db, estate.RepoName, estate.GitDir, estate.Branch, true, 3600, DateTime.UtcNow));

        var (_, request, _) = await TriggerEstate.RequestAsync(client, admin, new CreateTriggerRequest(estate.RepoName, Schedules: ["nightly"]));
        await TriggerEstate.WaitAsync(client, admin, request!.Id, d => d.Request.SyncRequestedUtc is not null);
        await estate.WithCatalogAsync(db => db.TriggerRequests.Where(r => r.Id == request.Id)
            .ExecuteUpdateAsync(r => r.SetProperty(x => x.SyncRequestedUtc, DateTime.UtcNow.AddMinutes(-31))));

        var done = await TriggerEstate.WaitForEndAsync(client, admin, request.Id);
        Assert.Equal(TriggerSyncOutcomes.TimedOut, done.Request.SyncOutcome);
        Assert.Contains("within 30 minutes", done.Request.Detail, StringComparison.Ordinal);
        Assert.Contains("ManagedSync:Enabled is false", done.Request.Detail, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task A_failed_run_fails_its_schedule_and_the_request_with_its_error()
    {
        var (cs, factory, client, admin) = await StartAsync();
        await using var _ = factory;
        using var __ = client;
        await using var estate = TriggerEstate.Seed(cs, "trg_fail", [new("land", "nightly"), new("load", "nightly")]);
        await estate.RegisterAsync(client, admin);
        var (_, request, _) = await TriggerEstate.RequestAsync(client, admin, new CreateTriggerRequest(estate.RepoName, Sync: false, Schedules: ["nightly"]));
        var running = await TriggerEstate.WaitAsync(client, admin, request!.Id, d => d.Request.State == TriggerRequestStates.Running);
        var runs = await estate.RunsAsync(running.Request.Schedules[0]);

        await estate.SetRunAsync(runs[0].RunId, RunStatuses.Running);
        await estate.WaitForTraceAsync(request.Id, e => e.Step == "run" && e.Message.Contains("started", StringComparison.Ordinal));
        await estate.SetRunAsync(runs[0].RunId, RunStatuses.Failed, "Login failed for user 'loader'.\nat Connect()");
        await estate.SetRunAsync(runs[1].RunId, RunStatuses.Skipped);

        var done = await TriggerEstate.WaitForEndAsync(client, admin, request.Id);
        Assert.Equal(TriggerRequestStates.Failed, done.Request.State);
        Assert.Equal("failure", done.Request.Conclusion);
        var schedule = done.Request.Schedules[0];
        Assert.Equal(TriggerScheduleStates.Failed, schedule.State);
        Assert.Equal(1, schedule.Counts.Failed);
        Assert.Equal(1, schedule.Counts.Skipped);
        Assert.Contains("Login failed", done.Runs.Single(r => r.Status == RunStatuses.Failed).Error, StringComparison.Ordinal);

        var trace = await estate.TraceAsync(request.Id);
        Assert.Single(trace, e => e.Step == "run" && e.Message.Contains("started", StringComparison.Ordinal));
        Assert.Single(trace, e => e.Step == "run" && e.Level == "error" && e.Message.Contains("Login failed for user 'loader'.", StringComparison.Ordinal));
        Assert.DoesNotContain(trace, e => e.Message.Contains("at Connect()", StringComparison.Ordinal));
        Assert.Single(trace, e => e.Step == "run" && e.Message.Contains("was skipped", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task Cancelling_a_request_cancels_its_queued_runs()
    {
        var (cs, factory, client, admin) = await StartAsync();
        await using var _ = factory;
        using var __ = client;
        await using var estate = TriggerEstate.Seed(cs, "trg_cancel", [new("land", "nightly"), new("load", "nightly")]);
        await estate.RegisterAsync(client, admin);
        var (_, request, _) = await TriggerEstate.RequestAsync(client, admin, new CreateTriggerRequest(estate.RepoName, Sync: false, Schedules: ["nightly"]));
        var running = await TriggerEstate.WaitAsync(client, admin, request!.Id, d => d.Request.State == TriggerRequestStates.Running);

        using (var cancel = await TriggerEstate.Send(client, admin, HttpMethod.Post, $"/api/v1/trigger-requests/{request.Id:D}/cancel"))
        {
            Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        }

        var done = await TriggerEstate.WaitForEndAsync(client, admin, request.Id);
        Assert.Equal(TriggerRequestStates.Cancelled, done.Request.State);
        Assert.Equal("cancelled", done.Request.Conclusion);
        Assert.Equal(TriggerScheduleStates.Cancelled, done.Request.Schedules[0].State);
        Assert.All(await estate.RunsAsync(running.Request.Schedules[0]), r => Assert.Equal(RunStatuses.Cancelled, r.Status));

        using var again = await TriggerEstate.Send(client, admin, HttpMethod.Post, $"/api/v1/trigger-requests/{request.Id:D}/cancel");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [SkippableFact]
    public async Task Asking_again_with_the_same_key_answers_with_the_request_already_made()
    {
        var (cs, factory, client, admin) = await StartAsync();
        await using var _ = factory;
        using var __ = client;
        await using var estate = TriggerEstate.Seed(cs, "trg_idem", [new("land", "nightly")]);
        await estate.RegisterAsync(client, admin);
        var body = new CreateTriggerRequest(estate.RepoName, Sync: false, Schedules: ["nightly"], RequestKey: "github-42-1");

        var first = await TriggerEstate.RequestAsync(client, admin, body);
        var second = await TriggerEstate.RequestAsync(client, admin, body);
        var parallel = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => TriggerEstate.RequestAsync(client, admin, body)));

        Assert.Equal(HttpStatusCode.Accepted, first.Status);
        Assert.Equal(HttpStatusCode.OK, second.Status);
        Assert.Equal(first.Request!.Id, second.Request!.Id);
        Assert.All(parallel, p => Assert.Equal(first.Request.Id, p.Request!.Id));
        var count = await estate.WithCatalogAsync(db => db.TriggerRequests.CountAsync(r => r.RepoName == estate.RepoName));
        Assert.Equal(1, count);
        var running = await TriggerEstate.WaitAsync(client, admin, first.Request.Id, d => d.Request.State == TriggerRequestStates.Running);
        Assert.Single(await estate.RunsAsync(running.Request.Schedules[0]));
    }

    [SkippableFact]
    public async Task A_control_plane_that_takes_over_a_fire_follows_the_runs_already_queued_instead_of_firing_again()
    {
        var (cs, factory, client, admin) = await StartAsync();
        await using var _ = factory;
        using var __ = client;
        await using var estate = TriggerEstate.Seed(cs, "trg_adopt", [new("land", "nightly"), new("load", "nightly")]);
        await estate.RegisterAsync(client, admin);
        var (_, request, _) = await TriggerEstate.RequestAsync(client, admin, new CreateTriggerRequest(estate.RepoName, Sync: false, Schedules: ["nightly"]));
        var running = await TriggerEstate.WaitAsync(client, admin, request!.Id, d => d.Request.State == TriggerRequestStates.Running);
        var groupId = running.Request.Schedules[0].GroupId;

        // As a replica that crashed right after its fire queued the runs would have left it: firing, with no run recorded.
        await estate.WithCatalogAsync(async db =>
        {
            await db.TriggerRequestSchedules.Where(s => s.RequestId == request.Id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.State, TriggerScheduleStates.Firing)
                .SetProperty(x => x.FiringUtc, DateTime.UtcNow.AddMinutes(-5))
                .SetProperty(x => x.RunId, (Guid?)null)
                .SetProperty(x => x.GroupId, (Guid?)null));
            return await db.TriggerRequests.Where(r => r.Id == request.Id)
                .ExecuteUpdateAsync(r => r.SetProperty(x => x.State, TriggerRequestStates.Firing));
        });

        var adopted = await TriggerEstate.WaitAsync(client, admin, request.Id, d => d.Request.State == TriggerRequestStates.Running && d.Request.Schedules[0].GroupId is not null);

        Assert.Equal(groupId, adopted.Request.Schedules[0].GroupId);
        var groups = await estate.WithCatalogAsync(db => db.RunGroups.CountAsync(g => g.RepoId == estate.RepoId));
        Assert.Equal(1, groups);
        var trace = await estate.TraceAsync(request.Id);
        Assert.Contains(trace, e => e.Message.Contains("had fired before this control plane took the request over", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task A_batch_narrows_the_fire_and_chain_off_holds_the_schedules_behind_it()
    {
        var (cs, factory, client, admin) = await StartAsync();
        await using var _ = factory;
        using var __ = client;
        await using var estate = TriggerEstate.Seed(
            cs, "trg_batch", [new("small_a", "nightly", "small"), new("small_b", "nightly", "small"), new("big", "nightly", "big"), new("after", "child")],
            "child: { after: nightly }");
        await estate.RegisterAsync(client, admin);

        var (_, request, _) = await TriggerEstate.RequestAsync(client, admin, new CreateTriggerRequest(
            estate.RepoName, Sync: false, Schedules: ["nightly"], Batch: ["small"], Chain: false));

        var running = await TriggerEstate.WaitAsync(client, admin, request!.Id, d => d.Request.State == TriggerRequestStates.Running);
        Assert.Equal(["small_a", "small_b"], running.Runs.Select(r => r.FlowName).Order(StringComparer.Ordinal));
        var child = await estate.WithCatalogAsync(db => db.Schedules.AsNoTracking().SingleAsync(s => s.RepoId == estate.RepoId && s.Name == "child"));
        Assert.NotNull(child.LastParentFireUtc);
    }

    [SkippableFact]
    public async Task Several_schedules_fire_in_one_request_and_the_request_ends_with_the_worst_of_them()
    {
        var (cs, factory, client, admin) = await StartAsync();
        await using var _ = factory;
        using var __ = client;
        await using var estate = TriggerEstate.Seed(cs, "trg_many", [new("land", "nightly"), new("load", "hourly")]);
        await estate.RegisterAsync(client, admin);
        var trigger = await estate.CreateTriggerAsync(client, admin, "all-" + estate.RepoName[^8..], allSchedules: true);
        var token = await TriggerEstate.ExchangeKeyAsync(client, trigger.Key!);

        var (_, request, _) = await TriggerEstate.RequestAsync(client, token, new CreateTriggerRequest(Sync: false, Schedules: ["nightly", "hourly"]));
        var running = await TriggerEstate.WaitAsync(client, token, request!.Id, d => d.Request.State == TriggerRequestStates.Running);
        await estate.EndRunsAsync(running.Request.Schedules.Single(s => s.Name == "nightly"), RunStatuses.Succeeded);
        await estate.EndRunsAsync(running.Request.Schedules.Single(s => s.Name == "hourly"), RunStatuses.Failed);

        var done = await TriggerEstate.WaitForEndAsync(client, token, request.Id);
        Assert.Equal(TriggerRequestStates.Failed, done.Request.State);
        Assert.Contains("Schedule 'hourly' did not succeed; 1 succeeded", done.Request.Detail, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task A_trigger_reads_its_own_requests_and_nothing_of_another_triggers()
    {
        var (cs, factory, client, admin) = await StartAsync();
        await using var _ = factory;
        using var __ = client;
        await using var estate = TriggerEstate.Seed(cs, "trg_own", [new("land", "nightly")]);
        await estate.RegisterAsync(client, admin);
        var mine = await estate.CreateTriggerAsync(client, admin, "mine-" + estate.RepoName[^8..], ["nightly"]);
        var theirs = await estate.CreateTriggerAsync(client, admin, "theirs-" + estate.RepoName[^8..], ["nightly"]);
        var myToken = await TriggerEstate.ExchangeKeyAsync(client, mine.Key!);
        var theirToken = await TriggerEstate.ExchangeKeyAsync(client, theirs.Key!);
        var (_, request, _) = await TriggerEstate.RequestAsync(client, myToken, new CreateTriggerRequest(Sync: false, Schedules: ["nightly"]));
        var running = await TriggerEstate.WaitAsync(client, myToken, request!.Id, d => d.Request.State == TriggerRequestStates.Running);
        var runId = running.Runs[0].RunId;

        foreach (var path in new[] { "", "/events", "/summary", $"/runs/{runId:D}/trace" })
        {
            using var mineRead = await TriggerEstate.Send(client, myToken, HttpMethod.Get, $"/api/v1/trigger-requests/{request.Id:D}{path}");
            using var theirRead = await TriggerEstate.Send(client, theirToken, HttpMethod.Get, $"/api/v1/trigger-requests/{request.Id:D}{path}");
            Assert.True(mineRead.IsSuccessStatusCode, $"{path}: {(int)mineRead.StatusCode}");
            Assert.Equal(HttpStatusCode.NotFound, theirRead.StatusCode);
        }

        using var theirCancel = await TriggerEstate.Send(client, theirToken, HttpMethod.Post, $"/api/v1/trigger-requests/{request.Id:D}/cancel");
        Assert.Equal(HttpStatusCode.NotFound, theirCancel.StatusCode);

        var theirList = await TriggerEstate.Get<PagedResult<TriggerRequestDto>>(client, theirToken, $"/api/v1/trigger-requests?triggerId={mine.Trigger.Id:D}");
        Assert.Empty(theirList.Items);
        var myList = await TriggerEstate.Get<PagedResult<TriggerRequestDto>>(client, myToken, "/api/v1/trigger-requests");
        Assert.Equal(request.Id, Assert.Single(myList.Items).Id);

        // The run's whole trace is text a caller keeps beside its job; a run the request did not start is not reachable.
        using var foreign = await TriggerEstate.Send(client, myToken, HttpMethod.Get, $"/api/v1/trigger-requests/{request.Id:D}/runs/{Guid.NewGuid():D}/trace");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [SkippableFact]
    public async Task A_disabled_trigger_makes_no_new_request_but_its_open_one_carries_on()
    {
        var (cs, factory, client, admin) = await StartAsync();
        await using var _ = factory;
        using var __ = client;
        await using var estate = TriggerEstate.Seed(cs, "trg_off", [new("land", "nightly")]);
        await estate.RegisterAsync(client, admin);
        var trigger = await estate.CreateTriggerAsync(client, admin, "off-" + estate.RepoName[^8..], ["nightly"]);
        var token = await TriggerEstate.ExchangeKeyAsync(client, trigger.Key!);
        var (_, request, _) = await TriggerEstate.RequestAsync(client, token, new CreateTriggerRequest(Sync: false, Schedules: ["nightly"]));
        var running = await TriggerEstate.WaitAsync(client, token, request!.Id, d => d.Request.State == TriggerRequestStates.Running);

        using (var disable = await TriggerEstate.Send(client, admin, HttpMethod.Post, $"/api/v1/external-triggers/{trigger.Trigger.Id:D}/disable"))
        {
            Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        }

        var refused = await TriggerEstate.RequestAsync(client, token, new CreateTriggerRequest(Sync: false, Schedules: ["nightly"]));
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        using (var exchange = await client.PostAsJsonAsync(new Uri("/api/v1/auth/trigger/key", UriKind.Relative), new KeyTriggerExchangeRequest(trigger.Key)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, exchange.StatusCode);
        }

        await estate.EndRunsAsync(running.Request.Schedules[0], RunStatuses.Succeeded);
        var done = await TriggerEstate.WaitForEndAsync(client, admin, request.Id);
        Assert.Equal(TriggerRequestStates.Succeeded, done.Request.State);
    }

    [SkippableFact]
    public async Task The_events_page_by_a_cursor_and_say_when_the_trace_has_ended()
    {
        var (cs, factory, client, admin) = await StartAsync();
        await using var _ = factory;
        using var __ = client;
        await using var estate = TriggerEstate.Seed(cs, "trg_events", [new("land", "nightly")]);
        await estate.RegisterAsync(client, admin);
        var (_, request, _) = await TriggerEstate.RequestAsync(client, admin, new CreateTriggerRequest(estate.RepoName, Sync: false, Schedules: ["nightly"]));
        var running = await TriggerEstate.WaitAsync(client, admin, request!.Id, d => d.Request.State == TriggerRequestStates.Running);

        var first = await TriggerEstate.Get<TriggerRequestEventsDto>(client, admin, $"/api/v1/trigger-requests/{request.Id:D}/events?limit=1");
        Assert.Single(first.Events);
        Assert.False(first.Done);
        var rest = await TriggerEstate.Get<TriggerRequestEventsDto>(client, admin, $"/api/v1/trigger-requests/{request.Id:D}/events?after={first.Next}");
        Assert.DoesNotContain(rest.Events, e => e.Id <= first.Next);
        Assert.False(rest.Done);

        await estate.EndRunsAsync(running.Request.Schedules[0], RunStatuses.Succeeded);
        await TriggerEstate.WaitForEndAsync(client, admin, request.Id);
        var all = await TriggerEstate.Get<TriggerRequestEventsDto>(client, admin, $"/api/v1/trigger-requests/{request.Id:D}/events");
        Assert.True(all.Done);
        Assert.True(all.Events[^1].Terminal);
        var after = await TriggerEstate.Get<TriggerRequestEventsDto>(client, admin, $"/api/v1/trigger-requests/{request.Id:D}/events?after={all.Next}");
        Assert.Empty(after.Events);
        Assert.True(after.Done);
        Assert.Equal(all.Next, after.Next);
    }

    [SkippableFact]
    public async Task A_local_path_source_has_no_commit_to_ask_for_but_syncs_and_fires()
    {
        var (cs, factory, client, admin) = await StartAsync();
        await using var _ = factory;
        using var __ = client;
        await using var estate = TriggerEstate.Seed(cs, "trg_local", [new("land", "nightly")]);
        using (var register = await TriggerEstate.Send(client, admin, HttpMethod.Post, "/api/v1/repos/sources",
            new RegisterRepoSourceRequest(estate.RepoName, null, estate.Branch, 3600, true, LocalPath: estate.GitDir)))
        {
            Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        }

        await estate.WaitForSyncAsync(client, admin, null);

        var withCommit = await TriggerEstate.RequestAsync(client, admin, new CreateTriggerRequest(estate.RepoName, Commit: "abcdef1"));
        Assert.Equal(HttpStatusCode.BadRequest, withCommit.Status);
        Assert.Contains("local directory", withCommit.Body, StringComparison.Ordinal);

        var (_, request, _) = await TriggerEstate.RequestAsync(client, admin, new CreateTriggerRequest(estate.RepoName, Schedules: ["nightly"]));
        var running = await TriggerEstate.WaitAsync(client, admin, request!.Id, d => d.Request.State == TriggerRequestStates.Running);
        Assert.StartsWith("worktree", running.Request.SyncedCommit, StringComparison.Ordinal);
    }
}
