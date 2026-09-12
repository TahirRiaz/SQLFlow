using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The data-stream board's read surface, and specifically the unit its evidence is counted in. Every measure
/// on that surface is a count of DAYS rather than of runs, which is what makes repeated runs harmless: a flow
/// run several times where the first takes the data and the rest find nothing new has one loading day, not one
/// loading run and several empty ones. The aggregation that decides it lives in an EF query whose translation
/// only a real database proves. Removes its own rows; the assembly runs serially (see AssemblyInfo).
/// </summary>
public sealed class DataStreamApiTests
{
    [Fact]
    public async Task DataStreams_WithoutAToken_Returns401()
    {
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/v1/datastreams", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A feed that ran six times every day and delivered on the first run of each: healthy for two months,
    /// then silent, with its last delivery falling before the analysed window. From inside the window it is
    /// indistinguishable from a reference table nobody changes, so the verdict rests on the history before it.
    /// <para>
    /// Counted in days that history reads "delivered on 60 of the 60 days it ran", which is an outage. Counted
    /// in RUNS the same history reads "delivered on 60 of 360 runs", which looks like a table that seldom
    /// changes and would excuse the outage as normal. That is the whole reason this is an endpoint test: the
    /// detector is handed two numbers and cannot tell which unit they were counted in.
    /// </para>
    /// </summary>
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task ADeadFeedRunSeveralTimesADay_IsStalled_NotExcusedAsARarelyChangingTable()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = FlowIdentity.FromName("ds_" + suffix);
        var flowName = "ds_flow_" + suffix;
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var today = DateTime.UtcNow.Date;

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Repos.Add(new CatalogRepo
                {
                    Id = repoId, Name = "ds_repo_" + suffix, FirstSeenUtc = today, LastSyncUtc = today,
                });
                db.Pipelines.Add(new CatalogPipeline
                {
                    Id = pipelineId,
                    RepoId = repoId,
                    Name = flowName,
                    Kind = "ing",
                    RelativePath = "flows/" + flowName + ".flow.yaml",
                    ContentHash = new string('0', 64),
                    Yaml = "name: " + flowName + "\n",
                    DefinitionJson = "{}",
                    Active = true,
                    Wave = 0,
                    FirstSeenUtc = today,
                    LastSeenUtc = today,
                });

                // Six runs a day throughout. Before the window the first run of each day delivers; inside it
                // every run succeeds and writes nothing.
                for (var dayOffset = 89; dayOffset >= 0; dayOffset--)
                {
                    var inWindow = dayOffset < 30;
                    for (var run = 0; run < 6; run++)
                    {
                        db.Runs.Add(new CatalogRun
                        {
                            RunId = Guid.NewGuid(),
                            PipelineId = pipelineId,
                            RepoId = repoId,
                            FlowName = flowName,
                            FlowKind = "ing",
                            Success = true,
                            Status = RunStatuses.Succeeded,
                            WrittenUtc = today.AddDays(-dayOffset).AddHours(run * 4),
                            DurationSeconds = 10,
                            RowsInserted = !inWindow && run == 0 ? 5_000 : 0,
                            RowsUpdated = 0,
                            RowsDeleted = 0,
                        });
                    }
                }

                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["read"]);
            var board = await GetJsonAsync<DataStreamsDto>(
                client, token, "/api/v1/datastreams?days=30&scope=all&includeUnscheduled=true&limit=500");

            var stream = Assert.Single(board.Streams, s => s.FlowName == flowName);

            // The unit itself, pinned: on the silent path the reported delivery share IS the prior evidence,
            // so 1.0 says it was counted in days and 0.17 would say it was counted in runs.
            Assert.Equal(1.0, stream.Profile.DeliveryShare);
            Assert.Equal("stalled", stream.Status);
            Assert.Equal("critical", stream.Severity);
            Assert.NotEqual("rarely-changes", stream.Category);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// The same shape read the other way: a table whose source genuinely changes a handful of times, running
    /// once a day. Its quiet window is what it has always looked like, so it must not be reported as an
    /// outage, and the day-counting above must not have cost the surface that reading.
    /// </summary>
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task AReferenceTableSilentSinceBeforeTheWindow_IsReportedAsRarelyChanging()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = FlowIdentity.FromName("dsr_" + suffix);
        var flowName = "dsr_flow_" + suffix;
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var today = DateTime.UtcNow.Date;

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Repos.Add(new CatalogRepo
                {
                    Id = repoId, Name = "dsr_repo_" + suffix, FirstSeenUtc = today, LastSyncUtc = today,
                });
                db.Pipelines.Add(new CatalogPipeline
                {
                    Id = pipelineId,
                    RepoId = repoId,
                    Name = flowName,
                    Kind = "ing",
                    RelativePath = "flows/" + flowName + ".flow.yaml",
                    ContentHash = new string('0', 64),
                    Yaml = "name: " + flowName + "\n",
                    DefinitionJson = "{}",
                    Active = true,
                    Wave = 0,
                    FirstSeenUtc = today,
                    LastSeenUtc = today,
                });

                // Ninety days of daily runs; the source changed on two of them, both before the window.
                for (var dayOffset = 89; dayOffset >= 0; dayOffset--)
                {
                    var changed = dayOffset is 80 or 45;
                    db.Runs.Add(new CatalogRun
                    {
                        RunId = Guid.NewGuid(),
                        PipelineId = pipelineId,
                        RepoId = repoId,
                        FlowName = flowName,
                        FlowKind = "ing",
                        Success = true,
                        Status = RunStatuses.Succeeded,
                        WrittenUtc = today.AddDays(-dayOffset).AddHours(5),
                        DurationSeconds = 10,
                        RowsInserted = 0,
                        RowsUpdated = changed ? 3 : 0,
                        RowsDeleted = 0,
                    });
                }

                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["read"]);
            var board = await GetJsonAsync<DataStreamsDto>(
                client, token, "/api/v1/datastreams?days=30&scope=all&includeUnscheduled=true&limit=500");

            var stream = Assert.Single(board.Streams, s => s.FlowName == flowName);

            // Two changes over the sixty days it ran before the window: the same measure, reading the other way.
            Assert.Equal(2.0 / 60, stream.Profile.DeliveryShare, 3);
            Assert.Equal("healthy", stream.Status);
            Assert.Equal("rarely-changes", stream.Category);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// The board can be asked about named flows only. The lineage graph asks this way: it has drawn a handful
    /// of flows and wants their verdicts, not the estate's. The filter has to be a real restriction on the
    /// query rather than a client-side slice of a full sweep, so this sets up two sibling flows in one repo and
    /// asks for one: the other must be absent because it was never analysed, which the counts prove by
    /// reporting a single analysed stream.
    /// </summary>
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task NamingAPipelineId_AnalysesThatStreamAlone()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = FlowIdentity.FromName("dsf_" + suffix);
        var wanted = "dsf_wanted_" + suffix;
        var other = "dsf_other_" + suffix;
        var wantedId = CatalogIdentity.Pipeline(repoId, wanted);
        var otherId = CatalogIdentity.Pipeline(repoId, other);
        var today = DateTime.UtcNow.Date;

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Repos.Add(new CatalogRepo
                {
                    Id = repoId, Name = "dsf_repo_" + suffix, FirstSeenUtc = today, LastSyncUtc = today,
                });
                foreach (var (id, name) in new[] { (wantedId, wanted), (otherId, other) })
                {
                    db.Pipelines.Add(new CatalogPipeline
                    {
                        Id = id,
                        RepoId = repoId,
                        Name = name,
                        Kind = "ing",
                        RelativePath = "flows/" + name + ".flow.yaml",
                        ContentHash = new string('0', 64),
                        Yaml = "name: " + name + "\n",
                        DefinitionJson = "{}",
                        Active = true,
                        Wave = 0,
                        FirstSeenUtc = today,
                        LastSeenUtc = today,
                    });

                    for (var dayOffset = 29; dayOffset >= 0; dayOffset--)
                    {
                        db.Runs.Add(new CatalogRun
                        {
                            RunId = Guid.NewGuid(),
                            PipelineId = id,
                            RepoId = repoId,
                            FlowName = name,
                            FlowKind = "ing",
                            Success = true,
                            Status = RunStatuses.Succeeded,
                            WrittenUtc = today.AddDays(-dayOffset).AddHours(5),
                            DurationSeconds = 10,
                            RowsInserted = 1_000,
                            RowsUpdated = 0,
                            RowsDeleted = 0,
                        });
                    }
                }

                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["read"]);
            var board = await GetJsonAsync<DataStreamsDto>(
                client,
                token,
                "/api/v1/datastreams?days=30&scope=all&includeUnscheduled=true&pipelineId=" + wantedId);

            Assert.Equal(1, board.AnalyzedStreams);
            var stream = Assert.Single(board.Streams);
            Assert.Equal(wanted, stream.FlowName);

            // The board's rows carry no day-by-day series; only the single-stream endpoint does. Naming a flow
            // must not quietly turn the board into that endpoint for every caller that filters.
            Assert.Null(stream.Series);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
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

    private static async Task<T> GetJsonAsync<T>(HttpClient client, string token, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(value);
        return value;
    }
}
