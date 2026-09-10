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
/// Deleting a repo: the store-level purge removes every repo-scoped row (and its runs' drill-down children) and the
/// managed git source registered under the same name, while leaving global objects and other repos untouched; the
/// endpoint enforces its route and reports the counts. DB-backed tests seed and remove their own rows; the assembly
/// runs serially (see AssemblyInfo).
/// </summary>
public sealed class RepoDeletionTests
{
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task DeleteAsync_PurgesEveryRepoScopedRow_DropsTheSource_AndLeavesGlobalAndOtherRepoRows()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoName = "del_" + suffix;
        var otherName = "keep_" + suffix;
        var repoId = FlowIdentity.FromName(repoName);
        var otherId = FlowIdentity.FromName(otherName);
        var sourceId = FlowIdentity.FromName($"reposource/{repoName}");
        var pipelineId = FlowIdentity.FromName(repoName + "/orders");
        var otherPipelineId = FlowIdentity.FromName(otherName + "/orders");
        var runId = Guid.NewGuid();
        var objectKey = "obj_" + suffix; // GLOBAL: shared across repos, must survive the delete
        var now = DateTime.UtcNow;

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Repos.Add(new CatalogRepo { Id = repoId, Name = repoName, FirstSeenUtc = now, LastSyncUtc = now });
                db.Repos.Add(new CatalogRepo { Id = otherId, Name = otherName, FirstSeenUtc = now, LastSyncUtc = now });

                db.Pipelines.Add(NewPipeline(pipelineId, repoId, repoName + "_orders", now));
                db.Pipelines.Add(NewPipeline(otherPipelineId, otherId, otherName + "_orders", now));

                db.Runs.Add(new CatalogRun
                {
                    RunId = runId, PipelineId = pipelineId, RepoId = repoId, FlowName = repoName + "_orders",
                    FlowKind = "ing", Status = RunStatuses.Succeeded, Success = true, WrittenUtc = now,
                });
                db.RunFiles.Add(new CatalogRunFile { RunId = runId, RepoId = repoId, Name = "data.csv", Rows = 1, Columns = 1 });
                db.RunStatements.Add(new CatalogRunStatement { RunId = runId, RepoId = repoId, Ordinal = 1, Step = "target.load", Sql = "SELECT 1" });
                db.RunEvents.Add(new CatalogRunEvent { RunId = runId, RepoId = repoId, Ordinal = 1, TimestampUtc = now, Level = "info", Message = "done" });
                db.RunGroups.Add(new CatalogRunGroup { GroupId = Guid.NewGuid(), RepoId = repoId, Mode = RunGroupModes.Batch, Anchor = repoName, MemberCount = 1, EnqueuedUtc = now });

                db.LineageEdges.Add(new CatalogLineageEdge { RepoId = repoId, PipelineId = pipelineId, Flow = repoName + "_orders", Relation = "Writes", ObjectKey = objectKey, ObjectName = "Orders", Tier = "Observed" });
                db.ObjectRelationships.Add(new CatalogObjectRelationship { RepoId = repoId, FromObjectKey = objectKey, FromColumns = "Id", ToObjectKey = objectKey, ToColumns = "Id", Origin = "Join", Tier = "Observed", Occurrences = 1 });
                db.FlowDependencies.Add(new CatalogFlowDependency { RepoId = repoId, FromFlow = "a", ToFlow = "b", FromPipelineId = pipelineId, ToPipelineId = pipelineId, ViaObjects = "Orders" });
                db.PipelineColumns.Add(new CatalogPipelineColumn { RepoId = repoId, PipelineId = pipelineId, Kind = PipelineColumnKinds.Declared, Ordinal = 1, ColumnName = "Id" });

                var scheduleId = Guid.NewGuid();
                db.Schedules.Add(new CatalogSchedule { Id = scheduleId, RepoId = repoId, Name = "nightly", Timezone = "UTC", Source = "api", IntervalSeconds = 3600, CreatedUtc = now, UpdatedUtc = now });
                db.ScheduleMembers.Add(new CatalogScheduleMember { ScheduleId = scheduleId, PipelineId = pipelineId, RepoId = repoId, FlowName = repoName + "_orders" });

                // Global object the deleted repo contributes to: another repo could reference it, so it must survive.
                db.Objects.Add(new CatalogObject { Key = objectKey, ServerRef = "@srv", Name = "Orders", Kind = "Table", FirstSeenUtc = now, LastSeenUtc = now });
                db.ActivityEvents.Add(new CatalogActivityEvent { ActivityId = Guid.NewGuid(), Kind = ActivityKinds.RepoSync, SubjectKey = sourceId.ToString(), Ordinal = 1, TimestampUtc = now, Level = "info", Message = "synced" });

                await db.SaveChangesAsync();

                // Upsert runs in its own serializable transaction (it clears the change tracker), so it is called
                // after the directly-added rows are already persisted, not interleaved with them.
                await RepoSourceStore.UpsertAsync(db, repoName, "https://example/repo.git", "main", enabled: true, syncIntervalSeconds: 3600, now);
            }

            RepoDeletionResult? result;
            await using (var db = CatalogDatabase.Create(cs))
            {
                result = await RepoStore.DeleteAsync(db, repoId);
            }

            Assert.NotNull(result);
            Assert.Equal(1, result.Pipelines);
            Assert.Equal(1, result.Runs);
            Assert.Equal(1, result.RunGroups);
            Assert.Equal(1, result.Schedules);
            Assert.Equal(1, result.LineageEdges);
            Assert.True(result.SourceRemoved);

            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.False(await db.Repos.AsNoTracking().AnyAsync(r => r.Id == repoId));
                Assert.False(await db.RepoSources.AsNoTracking().AnyAsync(s => s.Id == sourceId));
                Assert.False(await db.Pipelines.AsNoTracking().AnyAsync(p => p.RepoId == repoId));
                Assert.False(await db.Runs.AsNoTracking().AnyAsync(r => r.RepoId == repoId));
                Assert.False(await db.RunFiles.AsNoTracking().AnyAsync(f => f.RunId == runId));
                Assert.False(await db.RunStatements.AsNoTracking().AnyAsync(s => s.RunId == runId));
                Assert.False(await db.RunEvents.AsNoTracking().AnyAsync(e => e.RunId == runId));
                Assert.False(await db.RunGroups.AsNoTracking().AnyAsync(g => g.RepoId == repoId));
                Assert.False(await db.LineageEdges.AsNoTracking().AnyAsync(e => e.RepoId == repoId));
                Assert.False(await db.ObjectRelationships.AsNoTracking().AnyAsync(r => r.RepoId == repoId));
                Assert.False(await db.FlowDependencies.AsNoTracking().AnyAsync(d => d.RepoId == repoId));
                Assert.False(await db.PipelineColumns.AsNoTracking().AnyAsync(c => c.RepoId == repoId));
                Assert.False(await db.Schedules.AsNoTracking().AnyAsync(s => s.RepoId == repoId));
                Assert.False(await db.ScheduleMembers.AsNoTracking().AnyAsync(m => m.RepoId == repoId));
                Assert.False(await db.ActivityEvents.AsNoTracking().AnyAsync(e => e.SubjectKey == sourceId.ToString()));

                // The global object and the other repo's rows are untouched.
                Assert.True(await db.Objects.AsNoTracking().AnyAsync(o => o.Key == objectKey));
                Assert.True(await db.Pipelines.AsNoTracking().AnyAsync(p => p.RepoId == otherId));
                Assert.True(await db.Repos.AsNoTracking().AnyAsync(r => r.Id == otherId));
            }
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.Objects.Where(o => o.Key == objectKey).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == otherId || p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == otherId || r.Id == repoId).ExecuteDeleteAsync();
            await db.RepoSources.Where(s => s.Id == sourceId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task DeleteAsync_ForUnknownRepo_ReturnsNull()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        await using var db = CatalogDatabase.Create(cs);
        Assert.Null(await RepoStore.DeleteAsync(db, Guid.NewGuid()));
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task DeleteSource_RemovesASourceOnlyRow_AndReportsUnknown()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var name = "srconly_" + suffix;
        var id = FlowIdentity.FromName($"reposource/{name}");
        var now = DateTime.UtcNow;

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                await RepoSourceStore.UpsertAsync(db, name, "https://example/repo.git", "main", enabled: true, syncIntervalSeconds: 3600, now);
                db.ActivityEvents.Add(new CatalogActivityEvent { ActivityId = Guid.NewGuid(), Kind = ActivityKinds.RepoSync, SubjectKey = id.ToString(), Ordinal = 1, TimestampUtc = now, Level = "info", Message = "queued" });
                await db.SaveChangesAsync();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.True(await RepoSourceStore.DeleteAsync(db, id));
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.False(await db.RepoSources.AsNoTracking().AnyAsync(s => s.Id == id));
                Assert.False(await db.ActivityEvents.AsNoTracking().AnyAsync(e => e.SubjectKey == id.ToString()));
                Assert.False(await RepoSourceStore.DeleteAsync(db, id)); // already gone
            }
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.RepoSources.Where(s => s.Id == id).ExecuteDeleteAsync();
            await db.ActivityEvents.Where(e => e.SubjectKey == id.ToString()).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task DeleteRepoEndpoint_ForUnknownId_Returns404_UnderOperateScope()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["operate"]);

        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri($"/api/v1/repos/{Guid.NewGuid()}", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static CatalogPipeline NewPipeline(Guid id, Guid repoId, string name, DateTime now) => new()
    {
        Id = id,
        RepoId = repoId,
        Name = name,
        Kind = "ing",
        RelativePath = "flows/orders.flow.yaml",
        ExecutionMode = PipelineExecutionModes.Auto,
        Lifecycle = PipelineLifecycles.Production,
        ContentHash = "hash",
        Yaml = "name: x",
        DefinitionJson = "{}",
        Active = true,
        FirstSeenUtc = now,
        LastSeenUtc = now,
    };

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
