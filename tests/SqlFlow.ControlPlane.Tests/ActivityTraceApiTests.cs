using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The general-purpose activity trace (the reusable operation-trace mechanism behind the repository sync panel):
/// the <see cref="ActivityTrace"/> writer appends ordered events under one activity and prunes old activities per
/// subject, and the live stream (<c>GET /activities/stream</c>) pushes events as they land and ends with the
/// terminal status, exactly as the run trace stream does. Gated on a reachable catalog database.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ActivityTraceApiTests
{
    private const string Kind = ActivityKinds.RepoSync;

    [SkippableFact]
    public async Task Writer_AppendsOrderedEvents_AndPrunesToTheNewestActivitiesPerSubject()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var subject = Guid.NewGuid().ToString("N");

        try
        {
            // One activity: its lines are ordinal 1..n, the last is terminal and carries the outcome.
            Guid firstActivity;
            await using (var db = CatalogDatabase.Create(cs))
            {
                var trace = await ActivityTrace.BeginAsync(db, Kind, subject, TimeProvider.System);
                firstActivity = trace.ActivityId;
                await trace.InfoAsync("start", "started");
                await trace.WarnAsync("warning", "a warning");
                await trace.CompleteAsync(ActivityStatuses.Succeeded, "done");
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var rows = await db.ActivityEvents.AsNoTracking()
                    .Where(e => e.Kind == Kind && e.SubjectKey == subject && e.ActivityId == firstActivity)
                    .OrderBy(e => e.Id).ToListAsync();
                Assert.Equal(new[] { 1, 2, 3 }, rows.Select(r => r.Ordinal).ToArray());
                Assert.Equal("warning", rows[1].Level);
                Assert.True(rows[^1].Terminal);
                Assert.Equal(ActivityStatuses.Succeeded, rows[^1].Status);
                Assert.All(rows.Take(2), r => Assert.False(r.Terminal));
            }

            // Seven attempts for the same subject: only the newest five activities survive; the two oldest are pruned.
            var activityIds = new List<Guid> { firstActivity };
            for (var i = 0; i < 6; i++)
            {
                await using var db = CatalogDatabase.Create(cs);
                var trace = await ActivityTrace.BeginAsync(db, Kind, subject, TimeProvider.System);
                activityIds.Add(trace.ActivityId);
                await trace.InfoAsync("start", "started");
                await trace.CompleteAsync(ActivityStatuses.Succeeded, "done");
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var surviving = await db.ActivityEvents.AsNoTracking()
                    .Where(e => e.Kind == Kind && e.SubjectKey == subject)
                    .Select(e => e.ActivityId).Distinct().ToListAsync();
                Assert.Equal(5, surviving.Count);
                Assert.DoesNotContain(activityIds[0], surviving); // the two oldest were pruned
                Assert.DoesNotContain(activityIds[1], surviving);
                Assert.Contains(activityIds[^1], surviving); // the newest survives
            }
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.ActivityEvents.Where(e => e.SubjectKey == subject).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task Stream_PushesEventsAsTheyLand_ResumesFromCursor_AndEndsOnTerminal()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var subject = Guid.NewGuid().ToString("N");
        var t0 = new DateTime(2026, 7, 20, 8, 0, 0, DateTimeKind.Utc);

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;

        try
        {
            // A non-terminal line already held by the client (its id is the cursor): the stream must not replay it,
            // and being non-terminal it keeps the stream open waiting for the rest of the activity.
            long preSeededId;
            await using (var db = CatalogDatabase.Create(cs))
            {
                var seed = new CatalogActivityEvent
                {
                    ActivityId = Guid.NewGuid(), Kind = Kind, SubjectKey = subject, Ordinal = 1, TimestampUtc = t0,
                    Level = "info", Step = "queued", Message = "already on the client", Terminal = false,
                };
                db.ActivityEvents.Add(seed);
                await db.SaveChangesAsync(ct);
                preSeededId = seed.Id;
            }

            using var client = factory.CreateClient();
            var token = await IssueReadTokenAsync(client);

            using var response = await SendStreamAsync(
                client, token, $"/api/v1/activities/stream?kind={Kind}&subject={subject}&afterId={preSeededId}", ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var frames = new SseFrameReader(body);

            var attemptId = Guid.NewGuid();
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.ActivityEvents.Add(new CatalogActivityEvent
                {
                    ActivityId = attemptId, Kind = Kind, SubjectKey = subject, Ordinal = 1,
                    TimestampUtc = t0.AddSeconds(1), Level = "info", Step = "clone", Message = "Cloning remote.",
                });
                await db.SaveChangesAsync(ct);
            }

            var first = await frames.ReadAsync("entry", ct);
            var firstEntry = JsonSerializer.Deserialize<ActivityEventDto>(first, JsonWeb.Options);
            Assert.NotNull(firstEntry);
            Assert.Equal("Cloning remote.", firstEntry.Message);
            Assert.False(firstEntry.Terminal);

            // The terminal event ends the stream with exactly one end frame carrying its status.
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.ActivityEvents.Add(new CatalogActivityEvent
                {
                    ActivityId = attemptId, Kind = Kind, SubjectKey = subject, Ordinal = 2,
                    TimestampUtc = t0.AddSeconds(2), Level = "info", Step = "done", Message = "Sync complete.",
                    Terminal = true, Status = ActivityStatuses.Succeeded,
                });
                await db.SaveChangesAsync(ct);
            }

            var terminalEntry = JsonSerializer.Deserialize<ActivityEventDto>(await frames.ReadAsync("entry", ct), JsonWeb.Options);
            Assert.NotNull(terminalEntry);
            Assert.True(terminalEntry.Terminal);

            var end = await frames.ReadAsync("end", ct);
            var endPayload = JsonSerializer.Deserialize<ActivityStreamEndDto>(end, JsonWeb.Options);
            Assert.NotNull(endPayload);
            Assert.Equal(ActivityStatuses.Succeeded, endPayload.Status);
            Assert.True(await frames.AtEndAsync(ct), "the stream must close after the end frame");
        }
        finally
        {
            timeout.CancelAfter(Timeout.InfiniteTimeSpan);
            await using var db = CatalogDatabase.Create(cs);
            await db.ActivityEvents.Where(e => e.SubjectKey == subject).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task Stream_UnknownSubject_EndsIdleImmediately()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;

        using var client = factory.CreateClient();
        var token = await IssueReadTokenAsync(client);

        using var response = await SendStreamAsync(
            client, token, $"/api/v1/activities/stream?kind={Kind}&subject={Guid.NewGuid():N}", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var frames = new SseFrameReader(body);

        var end = await frames.ReadAsync("end", ct);
        var endPayload = JsonSerializer.Deserialize<ActivityStreamEndDto>(end, JsonWeb.Options);
        Assert.NotNull(endPayload);
        Assert.Equal("idle", endPayload.Status);
    }

    private static class JsonWeb
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    }

    private static async Task<string> IssueReadTokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static Task<HttpResponseMessage> SendStreamAsync(
        HttpClient client, string token, string relativeUri, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(relativeUri, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }
}
