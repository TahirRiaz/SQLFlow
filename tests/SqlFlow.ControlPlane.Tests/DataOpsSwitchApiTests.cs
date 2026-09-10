using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Compute;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The DataOps kill switch, in BOTH directions, across the whole surface it governs.
///
/// A feature switch is only worth having if turning it off actually turns everything off and turning it on
/// actually turns everything on. Both halves are asserted here because both have failed in this codebase for
/// the same reason: a hand-kept list of what the switch covers drifting away from what the switch should
/// cover. The third property matters just as much and is the easiest to break by accident: turning the switch
/// off must leave the REST of the product alone, so the pre-existing warehouse-health probes stay reachable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DataOpsSwitchApiTests
{
    /// <summary>Every operation the switch governs, taken from the operation set rather than listed here, so
    /// this test cannot pass by testing a stale subset of the surface.</summary>
    private static IReadOnlyList<string> GatedOperations()
        => ComputeOperations.All.Where(ComputeOperations.IsDataOps).ToArray();

    [SkippableFact]
    public async Task WithTheSwitchOff_EveryGatedOperationIsRefused_AndTheRestOfTheProductIsUntouched()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var (repoId, serverRef) = (Guid.NewGuid(), "${env:SQLFLOW_OFF_" + Guid.NewGuid().ToString("N")[..8] + "}");

        // No WithSetting: this is the shipped default, so the test also proves the feature ships OFF.
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        try
        {
            await SeedAsync(cs, repoId, serverRef);
            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client);

            // 1. Every gated compute operation is refused, and the refusal names the setting to change.
            foreach (var operation in GatedOperations())
            {
                using var response = await PostAsync(client, token, "/api/v1/datasources/tasks",
                    new { reference = serverRef, operation });

                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                var problem = await response.Content.ReadAsStringAsync();
                Assert.Contains("DataOps__Enabled", problem, StringComparison.Ordinal);
            }

            // 2. Both query endpoints are refused too, so the switch covers the surface and not just the queue.
            using (var prepare = await PostAsync(client, token, "/api/v1/dataops/queries/prepare",
                       new PrepareQueryRequest("SELECT 1", serverRef)))
            {
                Assert.Equal(HttpStatusCode.Forbidden, prepare.StatusCode);
            }

            using (var run = await PostAsync(
                       client, token, $"/api/v1/dataops/queries/{Guid.NewGuid()}/run", new { }))
            {
                Assert.Equal(HttpStatusCode.Forbidden, run.StatusCode);
            }

            // 3. Nothing was queued. A refusal must refuse, not queue a task that fails later on a node.
            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.Equal(0, await db.ComputeTasks.CountAsync(t => t.SourceRef == serverRef));
                Assert.Equal(0, await db.QueryPlans.CountAsync(p => p.SourceRef == serverRef));
            }

            // 4. Capabilities still ANSWERS while disabled, saying so. A client that cannot ask "is this on"
            //    has to guess, and guessing is what makes an assistant claim a capability does not exist.
            var capabilities = await GetJsonAsync<DataOpsCapabilitiesDto>(
                client, token, "/api/v1/dataops/capabilities");
            Assert.False(capabilities.Enabled);
            Assert.Contains("DataOps__Enabled", capabilities.DisabledReason, StringComparison.Ordinal);

            // 5. The rest of the product is untouched. The warehouse-health probes predate this surface and
            //    the insights dashboard reads their results, so the switch must not reach them.
            foreach (var probe in ComputeOperations.All.Where(ComputeOperations.IsWarehouseHealth))
            {
                using var response = await PostAsync(client, token, "/api/v1/datasources/tasks",
                    new { reference = serverRef, operation = probe });

                Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
            }
        }
        finally
        {
            await CleanupAsync(cs, repoId, serverRef);
        }
    }

    [SkippableFact]
    public async Task WithTheSwitchOn_EveryGatedOperationIsReachable_AndAdvertised()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var (repoId, serverRef) = (Guid.NewGuid(), "${env:SQLFLOW_ON_" + Guid.NewGuid().ToString("N")[..8] + "}");

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs)
            .WithSetting("ControlPlane:DataOps:Enabled", "true");
        try
        {
            await SeedAsync(cs, repoId, serverRef);
            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client);

            // 1. Capabilities ADVERTISES every gated operation. This is the check that was missing: the
            //    surface worked and the endpoint that describes it omitted runQuery, so the assistant read
            //    the description and correctly concluded the capability was absent.
            var capabilities = await GetJsonAsync<DataOpsCapabilitiesDto>(
                client, token, "/api/v1/dataops/capabilities");

            Assert.True(capabilities.Enabled);
            foreach (var operation in GatedOperations())
            {
                Assert.Contains(operation, capabilities.Operations);
            }

            Assert.Contains("prepare_query", capabilities.QuerySurface, StringComparison.Ordinal);

            // 2. The query surface accepts a real statement and mints a token.
            using var prepare = await PostAsync(client, token, "/api/v1/dataops/queries/prepare",
                new PrepareQueryRequest("SELECT 1 AS x", serverRef));
            prepare.EnsureSuccessStatusCode();

            // 3. Every gated compute operation is past the switch. They are validated on their own arguments
            //    after it, so a 400 here is the ARGUMENTS being wrong, never the feature being off; what
            //    matters is that none of them is refused as disabled.
            foreach (var operation in GatedOperations().Where(o => o != ComputeOperations.RunQuery))
            {
                using var response = await PostAsync(client, token, "/api/v1/datasources/tasks",
                    new { reference = serverRef, operation });

                Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
            }
        }
        finally
        {
            await CleanupAsync(cs, repoId, serverRef);
        }
    }

    private static async Task SeedAsync(string cs, Guid repoId, string serverRef)
    {
        var now = DateTime.UtcNow;
        await using var db = CatalogDatabase.Create(cs);
        db.Repos.Add(new CatalogRepo { Id = repoId, Name = "sw_" + repoId.ToString("N")[..8], FirstSeenUtc = now, LastSyncUtc = now });
        db.Pipelines.Add(new CatalogPipeline
        {
            Id = Guid.NewGuid(),
            RepoId = repoId,
            Name = "sw_flow_" + repoId.ToString("N")[..8],
            Kind = "ing",
            RelativePath = "sw/flow.yaml",
            Active = true,
            SourceServer = serverRef,
            TargetServer = serverRef,
            DefinitionJson = "{}",
            FirstSeenUtc = now,
            LastSeenUtc = now,
        });
        await db.SaveChangesAsync();
    }

    private static async Task CleanupAsync(string cs, Guid repoId, string serverRef)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.QueryPlans.Where(p => p.SourceRef == serverRef).ExecuteDeleteAsync();
        await db.ComputeTasks.Where(t => t.SourceRef == serverRef).ExecuteDeleteAsync();
        await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
    }

    private static async Task<string> IssueTokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read", "operate"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static async Task<HttpResponseMessage> PostAsync<T>(
        HttpClient client, string token, string relativeUri, T body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(relativeUri, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<T> GetJsonAsync<T>(HttpClient client, string token, string relativeUri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(relativeUri, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(value);
        return value;
    }
}
