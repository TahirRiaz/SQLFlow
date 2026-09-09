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
/// The authenticated catalog read API end to end against the real shadow catalog: the host runs in-memory through
/// <see cref="ControlPlaneAppFactory"/> pointed at the migrated catalog database, while the test seeds one repo and
/// two pipelines straight into the <c>catalog</c> schema with EF. It then obtains a bootstrap token and asserts the
/// repos list, the repo-filtered pipelines list, the pipeline detail (with Yaml/DefinitionJson), a 404 for an
/// unknown id, and readiness. Every seeded row is removed in a finally so repeated runs stay isolated.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CatalogReadApiIntegrationTests
{
    [SkippableFact]
    public async Task ReadApi_OverSeededCatalog_ReturnsReposPipelinesDetailAndHandlesMissing()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoName = "cp_api_" + suffix;
        var repoId = FlowIdentity.FromName(repoName);
        var flowA = "cp_orders_" + suffix;
        var flowB = "cp_customers_" + suffix;
        var flowGhost = "cp_ghost_" + suffix;
        var batchName = "sales_" + suffix;
        var pipelineAId = CatalogIdentity.Pipeline(repoId, flowA);
        var pipelineBId = CatalogIdentity.Pipeline(repoId, flowB);
        var now = DateTime.UtcNow;

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Repos.Add(new CatalogRepo
                {
                    Id = repoId,
                    Name = repoName,
                    RemoteUrl = "https://example/" + repoName + ".git",
                    RootPath = "/tmp/" + repoName,
                    FirstSeenUtc = now,
                    LastSyncUtc = now,
                });
                // Distinct projects: flowA under a named root folder, flowB at the repo root, so the project
                // filter and the projects endpoint below have both a named project and "(root)" to exercise.
                // flowA declares a batch while flowB declares none, so the batches endpoint and the batch
                // filter below have both a named batch and the default-batch coalescing to exercise.
                db.Pipelines.Add(SeedPipeline(pipelineAId, repoId, flowA, "ing", "sales/orders.flow.yaml", now, batchName));
                db.Pipelines.Add(SeedPipeline(pipelineBId, repoId, flowB, "file", "customers.flow.yaml", now));

                // Two flow dependencies: one between the seeded pipelines and one leading out of them, so the
                // pipelineId filter on the dependencies endpoint is proven to narrow rather than return all.
                db.FlowDependencies.Add(new CatalogFlowDependency
                {
                    RepoId = repoId, FromFlow = flowA, ToFlow = flowB,
                    FromPipelineId = pipelineAId, ToPipelineId = pipelineBId, ViaObjects = "dw.dbo.orders",
                });
                db.FlowDependencies.Add(new CatalogFlowDependency
                {
                    RepoId = repoId, FromFlow = flowB, ToFlow = flowGhost,
                    FromPipelineId = pipelineBId, ToPipelineId = CatalogIdentity.Pipeline(repoId, flowGhost),
                    ViaObjects = "dw.dbo.customers",
                });
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueReadTokenAsync(client);

            // /health/ready probes the catalog DbContext; with a real, migrated catalog it is healthy.
            using (var ready = await SendAsync(client, HttpMethod.Get, "/health/ready", token))
            {
                Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            }

            // The repos list is paginated and spans repos; find ours by name (it may share the catalog with others).
            var seededRepo = await FindRepoAsync(client, token, repoId);
            Assert.Equal(repoName, seededRepo.Name);
            Assert.Equal("https://example/" + repoName + ".git", seededRepo.RemoteUrl);

            // The repo-scoped pipelines list returns exactly the two we seeded.
            var pipelines = await GetJsonAsync<PagedResult<PipelineSummaryDto>>(
                client, token, $"/api/v1/pipelines?repoId={repoId}&pageSize=200");
            Assert.Equal(2, pipelines.Total);
            var names = pipelines.Items.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.Equal(new[] { flowB, flowA }.OrderBy(n => n, StringComparer.Ordinal), names);
            Assert.All(pipelines.Items, p => Assert.Equal(repoId, p.RepoId));

            // A kind filter narrows the same list to a single pipeline.
            var ingOnly = await GetJsonAsync<PagedResult<PipelineSummaryDto>>(
                client, token, $"/api/v1/pipelines?repoId={repoId}&kind=ing");
            Assert.Equal(1, ingOnly.Total);
            Assert.Equal(flowA, Assert.Single(ingOnly.Items).Name);

            // The projects endpoint lists the repo's distinct root folders: the named project and "(root)".
            var projects = await GetJsonAsync<List<string>>(client, token, $"/api/v1/pipelines/projects?repoId={repoId}");
            Assert.Equal(new[] { "(root)", "sales" }, projects);

            // The project filter narrows by root folder: "sales" is flowA, "(root)" is the repo-root flowB.
            var salesOnly = await GetJsonAsync<PagedResult<PipelineSummaryDto>>(
                client, token, $"/api/v1/pipelines?repoId={repoId}&project=sales");
            Assert.Equal(flowA, Assert.Single(salesOnly.Items).Name);
            var rootOnly = await GetJsonAsync<PagedResult<PipelineSummaryDto>>(
                client, token, $"/api/v1/pipelines?repoId={repoId}&project=(root)");
            Assert.Equal(flowB, Assert.Single(rootOnly.Items).Name);

            // The batches endpoint groups the repo's flows by batch, coalescing an undeclared batch into the
            // default label, each group with its flow counts.
            var batches = await GetJsonAsync<IReadOnlyList<PipelineBatchDto>>(
                client, token, $"/api/v1/pipelines/batches?repoId={repoId}");
            Assert.Equal(2, batches.Count);
            var named = batches.Single(b => b.Batch == batchName);
            Assert.Equal(1, named.FlowCount);
            Assert.Equal(1, named.ActiveCount);
            var fallback = batches.Single(b => b.Batch == CatalogPipeline.DefaultBatch);
            Assert.Equal(1, fallback.FlowCount);

            // The batch filter narrows the pipelines list; filtering by the default batch matches the flow
            // that declared no batch (the same coalescing the batches endpoint applies).
            var namedBatch = await GetJsonAsync<PagedResult<PipelineSummaryDto>>(
                client, token, $"/api/v1/pipelines?repoId={repoId}&batch={Uri.EscapeDataString(batchName)}");
            Assert.Equal(flowA, Assert.Single(namedBatch.Items).Name);
            var defaultBatch = await GetJsonAsync<PagedResult<PipelineSummaryDto>>(
                client, token, $"/api/v1/pipelines?repoId={repoId}&batch={CatalogPipeline.DefaultBatch}");
            Assert.Equal(flowB, Assert.Single(defaultBatch.Items).Name);

            // The dependencies list returns the repo's whole DAG unfiltered; the pipelineId filter narrows it
            // to the edges touching one flow, in either direction.
            var allDeps = await GetJsonAsync<PagedResult<FlowDependencyDto>>(
                client, token, $"/api/v1/repos/{repoId}/dependencies?pageSize=50");
            Assert.Equal(2, allDeps.Total);
            var flowADeps = await GetJsonAsync<PagedResult<FlowDependencyDto>>(
                client, token, $"/api/v1/repos/{repoId}/dependencies?pipelineId={pipelineAId}&pageSize=50");
            var onlyDep = Assert.Single(flowADeps.Items);
            Assert.Equal(flowA, onlyDep.FromFlow);
            Assert.Equal(flowB, onlyDep.ToFlow);

            // The detail view carries the heavy body: the redacted Yaml and the queryable DefinitionJson.
            var detail = await GetJsonAsync<PipelineDetailDto>(client, token, $"/api/v1/pipelines/{pipelineAId}");
            Assert.Equal(pipelineAId, detail.Id);
            Assert.Equal(flowA, detail.Name);
            Assert.Equal("ing", detail.Kind);
            Assert.Contains(flowA, detail.Yaml, StringComparison.Ordinal);
            Assert.Contains(flowA, detail.DefinitionJson, StringComparison.Ordinal);

            // The dedicated definition endpoint returns that same JSON body as application/json.
            using (var definition = await SendAsync(client, HttpMethod.Get, $"/api/v1/pipelines/{pipelineAId}/definition", token))
            {
                Assert.Equal(HttpStatusCode.OK, definition.StatusCode);
                Assert.Equal("application/json", definition.Content.Headers.ContentType?.MediaType);
                var json = await definition.Content.ReadAsStringAsync();
                Assert.Contains(flowA, json, StringComparison.Ordinal);
            }

            // An unknown pipeline id is a 404 with a ProblemDetails body.
            using (var missing = await SendAsync(client, HttpMethod.Get, $"/api/v1/pipelines/{Guid.NewGuid()}", token))
            {
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
                Assert.Equal("application/problem+json", missing.Content.Headers.ContentType?.MediaType);
                var problem = await missing.Content.ReadFromJsonAsync<ProblemPayload>();
                Assert.NotNull(problem);
                Assert.Equal(404, problem.Status);
                Assert.False(string.IsNullOrWhiteSpace(problem.Title));
            }
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.FlowDependencies.Where(d => d.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    private static CatalogPipeline SeedPipeline(
        Guid id, Guid repoId, string name, string kind, string relativePath, DateTime now, string? batch = null)
        => new()
        {
            Id = id,
            RepoId = repoId,
            Name = name,
            Kind = kind,
            Batch = batch,
            RelativePath = relativePath,
            ContentHash = "0000000000000000000000000000000000000000000000000000000000000000",
            Yaml = $"name: {name}\nflowType: {kind}\n",
            DefinitionJson = $$"""{"name":"{{name}}","flowType":"{{kind}}"}""",
            Active = true,
            Wave = 0,
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };

    private static async Task<RepoDto> FindRepoAsync(HttpClient client, string token, Guid repoId)
    {
        // Page through the repos list so the assertion holds even when the catalog already has many repos.
        const int pageSize = 200;
        for (var page = 1; ; page++)
        {
            var paged = await GetJsonAsync<PagedResult<RepoDto>>(client, token, $"/api/v1/repos?page={page}&pageSize={pageSize}");
            var match = paged.Items.FirstOrDefault(r => r.Id == repoId);
            if (match is not null)
            {
                return match;
            }

            Assert.True(paged.Items.Count > 0, $"repo {repoId} was not found in the repos list");
            if ((long)page * pageSize >= paged.Total)
            {
                Assert.Fail($"repo {repoId} was not found across {paged.Total} repos");
            }
        }
    }

    private static async Task<string> IssueReadTokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        Assert.False(string.IsNullOrWhiteSpace(token.AccessToken));
        return token.AccessToken;
    }

    private static async Task<T> GetJsonAsync<T>(HttpClient client, string token, string relativeUri)
    {
        using var response = await SendAsync(client, HttpMethod.Get, relativeUri, token);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(value);
        return value;
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string relativeUri, string token)
    {
        var request = new HttpRequestMessage(method, new Uri(relativeUri, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    /// <summary>The fields of an RFC 7807 ProblemDetails body the assertions read.</summary>
    private sealed record ProblemPayload(string? Title, int Status);
}
