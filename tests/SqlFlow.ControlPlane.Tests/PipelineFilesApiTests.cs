using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The pipeline-files endpoints over a seeded catalog: the files a pipeline has processed across its runs, one row
/// per distinct file (a file re-pulled by several runs is deduplicated), newest-modified first, searchable, and
/// each flagged <c>lastRun</c> when it was processed by the pipeline's most recent file-bearing run, plus the
/// size profile computed over that same deduplicated universe.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PipelineFilesApiTests
{
    [SkippableFact]
    public async Task Files_OverSeededRuns_Deduplicate_FlagLastRun_Sort_AndSearch()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = FlowIdentity.FromName("cp_files_" + suffix);
        var pipelineId = CatalogIdentity.Pipeline(repoId, "cp_land_" + suffix);
        var olderRun = Guid.NewGuid();
        var newerRun = Guid.NewGuid();

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Pipelines.Add(new CatalogPipeline
                {
                    Id = pipelineId,
                    RepoId = repoId,
                    Name = "cp_land_" + suffix,
                    Kind = "file",
                    RelativePath = "land.flow.yaml",
                    ContentHash = "hash",
                    Active = true,
                    FirstSeenUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                    LastSeenUtc = new DateTime(2024, 6, 2, 0, 0, 0, DateTimeKind.Utc),
                });
                db.Runs.Add(Run(olderRun, pipelineId, repoId, new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc)));
                db.Runs.Add(Run(newerRun, pipelineId, repoId, new DateTime(2024, 6, 2, 0, 0, 0, DateTimeKind.Utc)));

                // a.csv only in the older run; b.csv in both (re-pulled); c.csv only in the newer run.
                db.RunFiles.Add(File(olderRun, repoId, "a.csv", new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)));
                db.RunFiles.Add(File(olderRun, repoId, "b.csv", new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero)));
                db.RunFiles.Add(File(newerRun, repoId, "b.csv", new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero)));
                db.RunFiles.Add(File(newerRun, repoId, "c.csv", new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero)));
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueReadTokenAsync(client);

            var page = await GetJsonAsync<PagedResult<PipelineFileDto>>(
                client, token, $"/api/v1/pipelines/{pipelineId}/files");

            // Deduplicated: three distinct files (b.csv collapsed from two runs), newest-modified first.
            Assert.Equal(3, page.Total);
            Assert.Equal(["b.csv", "c.csv", "a.csv"], page.Items.Select(f => f.Name));

            // The newest run (newerRun) processed b.csv and c.csv; a.csv (older run only) is not flagged.
            Assert.True(page.Items.Single(f => f.Name == "b.csv").LastRun);
            Assert.True(page.Items.Single(f => f.Name == "c.csv").LastRun);
            Assert.False(page.Items.Single(f => f.Name == "a.csv").LastRun);

            // Search narrows by name.
            var search = await GetJsonAsync<PagedResult<PipelineFileDto>>(
                client, token, $"/api/v1/pipelines/{pipelineId}/files?search=c.csv");
            var only = Assert.Single(search.Items);
            Assert.Equal("c.csv", only.Name);

            // An unknown pipeline is a 404 (distinct from a real pipeline that has processed no files).
            using var missing = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/v1/pipelines/{Guid.NewGuid()}/files", UriKind.Relative));
            missing.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var missingResponse = await client.SendAsync(missing);
            Assert.Equal(System.Net.HttpStatusCode.NotFound, missingResponse.StatusCode);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.RunFiles.Where(f => f.RunId == olderRun || f.RunId == newerRun).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RunId == olderRun || r.RunId == newerRun).ExecuteDeleteAsync();
            await db.Pipelines.Where(pp => pp.Id == pipelineId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task FileStats_ProfileTheDeduplicatedFiles_AndReportZerosForAPipelineWithNone()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = FlowIdentity.FromName("cp_stats_" + suffix);
        var pipelineId = CatalogIdentity.Pipeline(repoId, "cp_stats_land_" + suffix);
        var emptyPipelineId = CatalogIdentity.Pipeline(repoId, "cp_stats_none_" + suffix);
        var olderRun = Guid.NewGuid();
        var newerRun = Guid.NewGuid();

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Pipelines.Add(Pipeline(pipelineId, repoId, "cp_stats_land_" + suffix));
                db.Pipelines.Add(Pipeline(emptyPipelineId, repoId, "cp_stats_none_" + suffix));
                db.Runs.Add(Run(olderRun, pipelineId, repoId, new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc)));
                db.Runs.Add(Run(newerRun, pipelineId, repoId, new DateTime(2024, 6, 2, 0, 0, 0, DateTimeKind.Utc)));

                // Four distinct files sized 100, 200, 300 and 1400 bytes; b.csv is re-pulled by the newer run and
                // must count once, so a re-run cannot re-weight the profile.
                db.RunFiles.Add(Sized(olderRun, repoId, "a.csv", 100, new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)));
                db.RunFiles.Add(Sized(olderRun, repoId, "b.csv", 200, new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero)));
                db.RunFiles.Add(Sized(newerRun, repoId, "b.csv", 200, new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero)));
                db.RunFiles.Add(Sized(newerRun, repoId, "c.csv", 300, new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero)));
                db.RunFiles.Add(Sized(newerRun, repoId, "d.csv", 1400, new DateTimeOffset(2024, 4, 1, 0, 0, 0, TimeSpan.Zero)));
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueReadTokenAsync(client);

            var stats = await GetJsonAsync<PipelineFileStatsDto>(
                client, token, $"/api/v1/pipelines/{pipelineId}/files/stats");

            Assert.Equal(4, stats.FileCount);
            Assert.Equal(2000, stats.TotalBytes);
            Assert.Equal(500, stats.AvgBytes);
            // Even count: the median straddles the two middle files (200 and 300).
            Assert.Equal(250, stats.MedianBytes);
            Assert.Equal(100, stats.MinBytes);
            Assert.Equal(1400, stats.MaxBytes);
            // Population standard deviation of {100, 200, 300, 1400} around 500 is sqrt(275000) = 524.4.
            Assert.Equal(524, stats.StdDevBytes);
            Assert.Equal(40, stats.TotalRows);
            Assert.Equal(10, stats.AvgRows);
            Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), stats.OldestModified);
            Assert.Equal(new DateTimeOffset(2024, 4, 1, 0, 0, 0, TimeSpan.Zero), stats.NewestModified);

            // Fewer files than the window, so the recent profile covers all four.
            Assert.NotNull(stats.Recent);
            Assert.Equal(4, stats.Recent.FileCount);
            Assert.Equal(500, stats.Recent.AvgBytes);
            Assert.Equal(100, stats.Recent.MinBytes);
            Assert.Equal(1400, stats.Recent.MaxBytes);

            // A real pipeline that has processed no files reports zeros with no window (not a 404).
            var empty = await GetJsonAsync<PipelineFileStatsDto>(
                client, token, $"/api/v1/pipelines/{emptyPipelineId}/files/stats");
            Assert.Equal(0, empty.FileCount);
            Assert.Equal(0, empty.TotalBytes);
            Assert.Equal(0, empty.AvgBytes);
            Assert.Null(empty.Recent);
            Assert.Null(empty.NewestModified);

            // An unknown pipeline is a 404.
            using var missing = new HttpRequestMessage(
                HttpMethod.Get, new Uri($"/api/v1/pipelines/{Guid.NewGuid()}/files/stats", UriKind.Relative));
            missing.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var missingResponse = await client.SendAsync(missing);
            Assert.Equal(System.Net.HttpStatusCode.NotFound, missingResponse.StatusCode);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.RunFiles.Where(f => f.RunId == olderRun || f.RunId == newerRun).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RunId == olderRun || r.RunId == newerRun).ExecuteDeleteAsync();
            await db.Pipelines.Where(pp => pp.Id == pipelineId || pp.Id == emptyPipelineId).ExecuteDeleteAsync();
        }
    }

    private static CatalogPipeline Pipeline(Guid pipelineId, Guid repoId, string name)
        => new()
        {
            Id = pipelineId,
            RepoId = repoId,
            Name = name,
            Kind = "file",
            RelativePath = name + ".flow.yaml",
            ContentHash = "hash",
            Active = true,
            FirstSeenUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            LastSeenUtc = new DateTime(2024, 6, 2, 0, 0, 0, DateTimeKind.Utc),
        };

    private static CatalogRun Run(Guid runId, Guid pipelineId, Guid repoId, DateTime startUtc)
        => new()
        {
            RunId = runId,
            PipelineId = pipelineId,
            RepoId = repoId,
            FlowName = "land",
            FlowKind = "file",
            Success = true,
            StartUtc = startUtc,
            EndUtc = startUtc,
            WrittenUtc = startUtc,
        };

    private static CatalogRunFile File(Guid runId, Guid repoId, string name, DateTimeOffset modified)
        => new()
        {
            RunId = runId,
            RepoId = repoId,
            Name = name,
            Path = "/data/" + name,
            Modified = modified,
            Rows = 1,
            Columns = 1,
            SizeBytes = 100,
        };

    private static CatalogRunFile Sized(Guid runId, Guid repoId, string name, long sizeBytes, DateTimeOffset modified)
        => new()
        {
            RunId = runId,
            RepoId = repoId,
            Name = name,
            Path = "/data/" + name,
            Modified = modified,
            Rows = 10,
            Columns = 1,
            SizeBytes = sizeBytes,
        };

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

    private static async Task<T> GetJsonAsync<T>(HttpClient client, string token, string relativeUri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(relativeUri, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(payload);
        return payload;
    }
}
