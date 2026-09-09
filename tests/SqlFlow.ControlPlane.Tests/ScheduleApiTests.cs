using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Runs;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The schedule API end to end through the in-memory host: authorization (any authenticated user may manage
/// schedules, since only user administration is scope-gated), validation (a malformed cron is a 400 before any
/// database work), the not-found path, the full create -> list ->
/// get -> pause -> resume -> delete lifecycle, and an end-to-end proof that the scheduler actually fires a due
/// schedule by enqueuing a run for its pipeline. DB-backed tests seed and remove their own repo's rows.
/// </summary>
public sealed class ScheduleApiTests
{
    [Fact]
    public async Task CreateSchedule_WithAnyAuthenticatedToken_IsAuthorized()
    {
        // Managing schedules is part of the operational product every authenticated user gets: only user
        // administration is scope-gated. So a token WITHOUT the operate scope still passes authorization and reaches
        // the endpoint's validation, which rejects a malformed cron with a 400 (proving it was not fenced off at 403).
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read"]);

        using var response = await PostAsync(client, token, "/api/v1/schedules",
            new CreateScheduleRequest(Guid.NewGuid(), ["flow"], "not a cron", null, "UTC", true));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateSchedule_WithMalformedCron_Returns400_BeforeTouchingTheDatabase()
    {
        // Validation runs before the pipeline lookup, so a bad cron is a 400 against the placeholder connection.
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["operate"]);

        using var response = await PostAsync(client, token, "/api/v1/schedules",
            new CreateScheduleRequest(Guid.NewGuid(), ["flow"], "not a cron", null, "UTC", true));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task CreateSchedule_ForUnknownPipeline_Returns404()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["operate"]);

        using var response = await PostAsync(client, token, "/api/v1/schedules",
            new CreateScheduleRequest(FlowIdentity.FromName("nope_" + Guid.NewGuid().ToString("N")), ["no_such"], "0 6 * * *", null, "UTC", true));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task CreateSchedule_ForActivePipeline_RunsTheFullLifecycle()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await SeedActivePipeline(cs, repoId, flowName);
            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["operate"]);

            // Create.
            Guid id;
            using (var create = await PostAsync(client, token, "/api/v1/schedules",
                new CreateScheduleRequest(repoId, [flowName], "0 6 * * *", null, "Europe/Oslo", true)))
            {
                Assert.Equal(HttpStatusCode.Created, create.StatusCode);
                var created = await create.Content.ReadFromJsonAsync<ScheduleCreated>();
                Assert.NotNull(created);
                Assert.NotEqual(Guid.Empty, created.Id);
                Assert.NotNull(created.NextFireUtc);
                id = created.Id;
            }

            // Get + list reflect it.
            var got = await GetJsonAsync<ScheduleDto>(client, token, $"/api/v1/schedules/{id}");
            Assert.Equal("api", got.Source);
            Assert.False(got.Paused);
            Assert.Equal("Europe/Oslo", got.Timezone);

            var list = await GetJsonAsync<PagedResult<ScheduleDto>>(client, token, $"/api/v1/schedules?repoId={repoId}");
            Assert.Contains(list.Items, s => s.Id == id);

            // Pause then resume toggles the operational flag and re-arms the next fire.
            using (var pause = await PostAsync(client, token, $"/api/v1/schedules/{id}/pause", null))
            {
                Assert.Equal(HttpStatusCode.OK, pause.StatusCode);
                var paused = await pause.Content.ReadFromJsonAsync<ScheduleDto>();
                Assert.True(paused!.Paused);
            }

            using (var resume = await PostAsync(client, token, $"/api/v1/schedules/{id}/resume", null))
            {
                Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
                var resumed = await resume.Content.ReadFromJsonAsync<ScheduleDto>();
                Assert.False(resumed!.Paused);
                Assert.NotNull(resumed.NextFireUtc);
            }

            // Delete, then it is gone.
            using (var del = await SendAsync(client, token, HttpMethod.Delete, $"/api/v1/schedules/{id}"))
            {
                Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
            }

            using var afterDelete = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/schedules/{id}");
            Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [Fact]
    public async Task RunScheduleNow_WithAnyAuthenticatedToken_IsAuthorized()
    {
        // Firing a schedule on demand is part of the operational product every authenticated user gets: only user
        // administration is scope-gated. A token WITHOUT the operate scope therefore clears authorization (the
        // response is neither 401 nor 403); the schedule lookup itself happens past that boundary.
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read"]);

        using var response = await PostAsync(client, token, $"/api/v1/schedules/{Guid.NewGuid()}/run", null);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task RunScheduleNow_ForUnknownSchedule_Returns404()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["operate"]);

        using var response = await PostAsync(client, token, $"/api/v1/schedules/{Guid.NewGuid()}/run", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task RunScheduleNow_EnqueuesARun_StampsLastRun_AndLeavesTheCadence()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await SeedActivePipeline(cs, repoId, flowName);
            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["operate"]);

            // A schedule whose next fire is far in the future, so the automatic scheduler will not fire it during the
            // test; only the explicit run-now should enqueue a run.
            Guid scheduleId;
            using (var create = await PostAsync(client, token, "/api/v1/schedules",
                new CreateScheduleRequest(repoId, [flowName], "0 6 1 1 *", null, "UTC", true)))
            {
                Assert.Equal(HttpStatusCode.Created, create.StatusCode);
                var created = await create.Content.ReadFromJsonAsync<ScheduleCreated>();
                Assert.NotNull(created);
                scheduleId = created.Id;
            }

            var beforeNextFire = (await GetJsonAsync<ScheduleDto>(client, token, $"/api/v1/schedules/{scheduleId}")).NextFireUtc;

            // Run now: 202 with the enqueued run id.
            Guid runId;
            using (var run = await PostAsync(client, token, $"/api/v1/schedules/{scheduleId}/run", null))
            {
                Assert.Equal(HttpStatusCode.Accepted, run.StatusCode);
                var accepted = await run.Content.ReadFromJsonAsync<ScheduleRunAccepted>();
                Assert.NotNull(accepted);
                Assert.NotEqual(Guid.Empty, accepted.RunId);
                runId = accepted.RunId;
            }

            // The run exists for the schedule's pipeline, the schedule's last run points at it, and its cadence is
            // untouched (the next scheduled fire did not move).
            var after = await GetJsonAsync<ScheduleDto>(client, token, $"/api/v1/schedules/{scheduleId}");
            Assert.Equal(runId, after.LastRunId);
            Assert.Equal(beforeNextFire, after.NextFireUtc);

            var runs = await GetJsonAsync<PagedResult<RunSummaryDto>>(client, token, $"/api/v1/runs?pipelineId={pipelineId}");
            Assert.Contains(runs.Items, r => r.RunId == runId);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task RunScheduleNow_WithABatchFilter_FiresOnlyThatBatchsMembers()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var alphaOne = flowName + "_a1";
        var alphaTwo = flowName + "_a2";

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            // Three members across two batches: the unlabelled flow (the default batch) and two carrying "alpha".
            await SeedActivePipeline(cs, repoId, flowName);
            await SeedBatchedPipeline(cs, repoId, alphaOne, "alpha");
            await SeedBatchedPipeline(cs, repoId, alphaTwo, "alpha");
            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["operate"]);

            Guid scheduleId;
            using (var create = await PostAsync(client, token, "/api/v1/schedules",
                new CreateScheduleRequest(repoId, [flowName, alphaOne, alphaTwo], "0 6 1 1 *", null, "UTC", true)))
            {
                Assert.Equal(HttpStatusCode.Created, create.StatusCode);
                var created = await create.Content.ReadFromJsonAsync<ScheduleCreated>();
                Assert.NotNull(created);
                scheduleId = created.Id;
            }

            // ?batch=alpha narrows the fire to the two labelled members. The parameter rides on the query string of a
            // POST with no body, exactly as the run board sends it, so this also pins the binding: an array parameter
            // that fell back to body binding would arrive null and quietly fire the whole schedule.
            using (var run = await PostAsync(client, token, $"/api/v1/schedules/{scheduleId}/run?batch=alpha", null))
            {
                Assert.Equal(HttpStatusCode.Accepted, run.StatusCode);
                var accepted = await run.Content.ReadFromJsonAsync<ScheduleRunAccepted>();
                Assert.NotNull(accepted);
                Assert.Equal(2, accepted.MemberCount);
                Assert.NotNull(accepted.GroupId);
            }

            // Only the alpha flows were enqueued; the default-batch member of the same schedule stayed put.
            var runs = await GetJsonAsync<PagedResult<RunSummaryDto>>(
                client, token, $"/api/v1/runs?repoId={repoId}&pageSize=200");
            Assert.Contains(runs.Items, r => r.FlowName == alphaOne);
            Assert.Contains(runs.Items, r => r.FlowName == alphaTwo);
            Assert.DoesNotContain(runs.Items, r => r.FlowName == flowName);

            // A batch no member carries is a 409, not a silent whole-schedule fire.
            using var unmatched = await PostAsync(client, token, $"/api/v1/schedules/{scheduleId}/run?batch=nosuch", null);
            Assert.Equal(HttpStatusCode.Conflict, unmatched.StatusCode);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Scheduler_FiresADueSchedule_EnqueuingARunForThePipeline()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var scheduleId = Guid.NewGuid();

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await SeedActivePipeline(cs, repoId, flowName);

            // A schedule already past due, with a daily cron so it fires exactly once during the test (the fire
            // advances the next occurrence to tomorrow). Seeded directly so we control the due time.
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Schedules.Add(new CatalogSchedule
                {
                    Id = scheduleId,
                    RepoId = repoId,
                    Name = flowName,

                    Cron = "0 6 * * *",
                    Timezone = "UTC",
                    Enabled = true,
                    Source = "api",
                    NextFireUtc = DateTime.UtcNow.AddMinutes(-1),
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow,
                });
                // Membership is the only selector for what a fire runs, so the flow has to have joined: a schedule
                // with no members resolves to nothing and correctly enqueues nothing.
                db.ScheduleMembers.Add(new CatalogScheduleMember
                {
                    ScheduleId = scheduleId,
                    PipelineId = pipelineId,
                    RepoId = repoId,
                    FlowName = flowName,
                });
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["operate"]);

            // The scheduler (1s tick in tests) should fire the schedule: its LastRunId gets set and its next fire
            // advances into the future.
            ScheduleDto? schedule = null;
            for (var attempt = 0; attempt < 60; attempt++)
            {
                schedule = await GetJsonAsync<ScheduleDto>(client, token, $"/api/v1/schedules/{scheduleId}");
                if (schedule.LastRunId is not null)
                {
                    break;
                }

                await Task.Delay(250);
            }

            Assert.NotNull(schedule);
            Assert.NotNull(schedule.LastRunId);
            Assert.NotNull(schedule.NextFireUtc);
            Assert.True(schedule.NextFireUtc > DateTime.UtcNow, "the next fire should have advanced into the future");

            // A run was enqueued for the schedule's pipeline.
            var runs = await GetJsonAsync<PagedResult<RunSummaryDto>>(client, token, $"/api/v1/runs?pipelineId={pipelineId}");
            Assert.True(runs.Total >= 1, "the fire should have enqueued at least one run for the pipeline");
            Assert.Contains(runs.Items, r => r.RunId == schedule.LastRunId);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task ScheduleList_ReportsHowTheLastFireEnded()
    {
        // "Did the last execution work" must be answerable from the list: the schedule row carries the tally of the
        // fire's members, so a group with one failure reads as failed rather than merely "fired".
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var scheduleId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var firstRunId = Guid.NewGuid();

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await SeedActivePipeline(cs, repoId, flowName);
            var now = DateTime.UtcNow;
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Schedules.Add(new CatalogSchedule
                {
                    Id = scheduleId,
                    RepoId = repoId,
                    Name = flowName,
                    // Far in the future, so the scheduler cannot fire it mid-test and rewrite the last-fire pointers.
                    Cron = "0 6 1 1 *",
                    Timezone = "UTC",
                    Enabled = true,
                    Source = "api",
                    NextFireUtc = now.AddYears(1),
                    LastFireUtc = now,
                    LastRunId = firstRunId,
                    LastGroupId = groupId,
                    CreatedUtc = now,
                    UpdatedUtc = now,
                });
                db.Runs.Add(SeedGroupRun(firstRunId, pipelineId, repoId, flowName, groupId, RunStatuses.Succeeded, now));
                db.Runs.Add(SeedGroupRun(Guid.NewGuid(), pipelineId, repoId, flowName, groupId, RunStatuses.Succeeded, now));
                db.Runs.Add(SeedGroupRun(Guid.NewGuid(), pipelineId, repoId, flowName, groupId, RunStatuses.Failed, now));
                db.Runs.Add(SeedGroupRun(Guid.NewGuid(), pipelineId, repoId, flowName, groupId, RunStatuses.Skipped, now));
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["read"]);

            var schedule = await GetJsonAsync<ScheduleDto>(client, token, $"/api/v1/schedules/{scheduleId}");
            Assert.NotNull(schedule.LastCounts);
            Assert.Equal(4, schedule.LastCounts.Total);
            Assert.Equal(2, schedule.LastCounts.Succeeded);
            Assert.Equal(1, schedule.LastCounts.Failed);
            Assert.Equal(1, schedule.LastCounts.Skipped);
            // Every member is terminal, so the fire is over: no live re-entry point.
            Assert.False(schedule.LastGroupActive);

            // The list projection answers the same way (it is the same pass over the page).
            var list = await GetJsonAsync<PagedResult<ScheduleDto>>(client, token, $"/api/v1/schedules?repoId={repoId}");
            var listed = Assert.Single(list.Items);
            Assert.Equal(1, listed.LastCounts?.Failed);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task ScheduleDefinition_ServesTheDeclaringYaml_OrSaysThereIsNone()
    {
        // The three shapes a definition can take: an inline block (serve the declaring flow's stored document), a
        // schedules.yaml entry (serve the library text on the row), and an API schedule (no file, so no YAML).
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var inlineId = Guid.NewGuid();
        var libraryId = Guid.NewGuid();
        var libraryYaml = "schedules:\n  nightly: { cron: \"0 4 * * *\", timezone: \"Europe/Oslo\" }\n";

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await SeedActivePipeline(cs, repoId, flowName);
            var now = DateTime.UtcNow;
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Schedules.Add(new CatalogSchedule
                {
                    Id = inlineId, RepoId = repoId, Name = flowName + "_inline", Cron = "0 4 * * *", Timezone = "UTC",
                    Enabled = true, Source = "yaml", NextFireUtc = now.AddYears(1),
                    DefinitionPath = "flows/" + flowName + ".flow.yaml", DefinitionFlow = flowName,
                    CreatedUtc = now, UpdatedUtc = now,
                });
                db.Schedules.Add(new CatalogSchedule
                {
                    Id = libraryId, RepoId = repoId, Name = flowName + "_library", Cron = "0 4 * * *", Timezone = "UTC",
                    Enabled = true, Source = "yaml", NextFireUtc = now.AddYears(1),
                    DefinitionPath = "schedules.yaml", DefinitionYaml = libraryYaml,
                    CreatedUtc = now, UpdatedUtc = now,
                });
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["read"]);

            var inline = await GetJsonAsync<ScheduleDefinitionDto>(client, token, $"/api/v1/schedules/{inlineId}/definition");
            Assert.Equal(flowName, inline.FlowName);
            Assert.Equal(CatalogIdentity.Pipeline(repoId, flowName), inline.PipelineId);
            Assert.Equal("flows/" + flowName + ".flow.yaml", inline.Path);
            Assert.Equal("name: " + flowName + "\n", inline.Yaml);

            var library = await GetJsonAsync<ScheduleDefinitionDto>(client, token, $"/api/v1/schedules/{libraryId}/definition");
            Assert.Null(library.FlowName);
            Assert.Null(library.PipelineId);
            Assert.Equal("schedules.yaml", library.Path);
            Assert.Equal(libraryYaml, library.Yaml);

            // An ad-hoc API schedule has no file behind it, and says so rather than inventing one.
            Guid apiId;
            using (var create = await PostAsync(client, token, "/api/v1/schedules",
                new CreateScheduleRequest(repoId, [flowName], "0 6 1 1 *", null, "UTC", true)))
            {
                Assert.Equal(HttpStatusCode.Created, create.StatusCode);
                var created = await create.Content.ReadFromJsonAsync<ScheduleCreated>();
                Assert.NotNull(created);
                apiId = created.Id;
            }

            var api = await GetJsonAsync<ScheduleDefinitionDto>(client, token, $"/api/v1/schedules/{apiId}/definition");
            Assert.Equal("api", api.Source);
            Assert.Null(api.Path);
            Assert.Null(api.Yaml);

            using var unknown = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/schedules/{Guid.NewGuid()}/definition");
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task ListSchedules_WithSearch_MatchesTheNameAcrossPages()
    {
        // The list page's search box: it must narrow the QUERY, not the page in hand, or a match sitting on page 4 of
        // an estate's schedules would look like no match at all. Proved with a page of one: the term selects its
        // schedule out of three even though only one row fits on a page, and the total counts only the matches.
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await SeedActivePipeline(cs, repoId, flowName);
            var now = DateTime.UtcNow;
            await using (var db = CatalogDatabase.Create(cs))
            {
                foreach (var suffix in new[] { "_alpha_daily", "_beta_daily", "_beta_hourly" })
                {
                    db.Schedules.Add(new CatalogSchedule
                    {
                        Id = Guid.NewGuid(), RepoId = repoId, Name = flowName + suffix, Cron = "0 4 * * *",
                        Timezone = "UTC", Enabled = true, Source = "yaml", NextFireUtc = now.AddYears(1),
                        CreatedUtc = now, UpdatedUtc = now,
                    });
                }

                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["read"]);

            var beta = await GetJsonAsync<PagedResult<ScheduleDto>>(
                client, token, $"/api/v1/schedules?repoId={repoId}&search=beta&pageSize=1");
            Assert.Equal(2L, beta.Total);
            Assert.Single(beta.Items);
            Assert.Equal(flowName + "_beta_daily", beta.Items[0].Name);

            // The second page of the same filtered list is the other match, not an unfiltered row.
            var betaPageTwo = await GetJsonAsync<PagedResult<ScheduleDto>>(
                client, token, $"/api/v1/schedules?repoId={repoId}&search=beta&pageSize=1&page=2");
            Assert.Equal(flowName + "_beta_hourly", Assert.Single(betaPageTwo.Items).Name);

            // A term nothing carries is an empty page, never a silent fall back to the whole list.
            var none = await GetJsonAsync<PagedResult<ScheduleDto>>(
                client, token, $"/api/v1/schedules?repoId={repoId}&search=nosuchschedule");
            Assert.Equal(0L, none.Total);
            Assert.Empty(none.Items);

            // Whitespace is not a filter: a blank term lists everything, so clearing the box restores the list.
            var blank = await GetJsonAsync<PagedResult<ScheduleDto>>(
                client, token, $"/api/v1/schedules?repoId={repoId}&search=%20");
            Assert.Equal(3L, blank.Total);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    private static CatalogRun SeedGroupRun(
        Guid runId, Guid pipelineId, Guid repoId, string flowName, Guid groupId, string status, DateTime writtenUtc)
        => new()
        {
            RunId = runId,
            PipelineId = pipelineId,
            RepoId = repoId,
            FlowName = flowName,
            FlowKind = "file",
            GroupId = groupId,
            Status = status,
            Success = status == RunStatuses.Succeeded,
            WrittenUtc = writtenUtc,
        };

    private static async Task SeedActivePipeline(string cs, Guid repoId, string flowName)
    {
        var now = DateTime.UtcNow;
        await using var db = CatalogDatabase.Create(cs);
        db.Repos.Add(new CatalogRepo
        {
            Id = repoId,
            Name = "sch_repo_" + repoId.ToString("N")[..8],
            RootPath = Path.Combine(Path.GetTempPath(), "sch_" + repoId.ToString("N")[..8]),
            FirstSeenUtc = now,
            LastSyncUtc = now,
        });
        db.Pipelines.Add(new CatalogPipeline
        {
            Id = CatalogIdentity.Pipeline(repoId, flowName),
            RepoId = repoId,
            Name = flowName,
            Kind = "file",
            RelativePath = "flows/" + flowName + ".flow.yaml",
            ContentHash = new string('0', 64),
            Yaml = "name: " + flowName + "\n",
            DefinitionJson = "{}",
            Active = true,
            Wave = 0,
            FirstSeenUtc = now,
            LastSeenUtc = now,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Adds one more active flow to a repo already seeded by <see cref="SeedActivePipeline"/>, carrying a
    /// batch label so a fire can be narrowed to it.</summary>
    private static async Task SeedBatchedPipeline(string cs, Guid repoId, string flowName, string batch)
    {
        var now = DateTime.UtcNow;
        await using var db = CatalogDatabase.Create(cs);
        db.Pipelines.Add(new CatalogPipeline
        {
            Id = CatalogIdentity.Pipeline(repoId, flowName),
            RepoId = repoId,
            Name = flowName,
            Kind = "file",
            Batch = batch,
            RelativePath = "flows/" + flowName + ".flow.yaml",
            ContentHash = new string('0', 64),
            Yaml = "name: " + flowName + "\n",
            DefinitionJson = "{}",
            Active = true,
            Wave = 0,
            FirstSeenUtc = now,
            LastSeenUtc = now,
        });
        await db.SaveChangesAsync();
    }

    private static (Guid RepoId, string FlowName) NewIds()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return (FlowIdentity.FromName("sch_" + suffix), "sch_flow_" + suffix);
    }

    private static async Task Cleanup(string cs, Guid repoId)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.ScheduleMembers.Where(m => m.RepoId == repoId).ExecuteDeleteAsync();
        await db.Schedules.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
        await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
        await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string token, string url, object? body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string token, HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private static async Task<T> GetJsonAsync<T>(HttpClient client, string token, string url)
    {
        using var response = await SendAsync(client, token, HttpMethod.Get, url);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(value);
        return value;
    }

    private static async Task<string> IssueTokenAsync(HttpClient client, IReadOnlyList<string> scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }
}
