using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The datasource surface end to end through the in-memory host: the estate-derived datasource list (with the
/// provider kind read out of the flow definitions), the compute-task trust boundary (scope, unknown operation,
/// inline connection strings, unknown references), pool routing (a routed task stays queued for its pool), task
/// cancellation, and the full happy path: trigger a listObjects task against the test catalog database itself
/// and read the live table listing back through the task result. The contract is references-only throughout.
/// </summary>
public sealed class DatasourceApiTests
{
    [Fact]
    public async Task TriggerTask_WithAnyAuthenticatedToken_IsAuthorized()
    {
        // Ad-hoc compute against a live source is part of the operational product every authenticated user gets:
        // only user administration is scope-gated. A token WITHOUT the operate scope therefore passes authorization
        // and reaches the endpoint's body validation, which rejects an operation outside the closed set with a 400
        // (proving it was not fenced off at 403).
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read"]);

        using var response = await PostTaskAsync(client, token, new { reference = "${env:X}", operation = "dropDatabase" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    // An operation outside the closed set.
    [InlineData("""{"reference":"${env:X}","operation":"dropDatabase"}""")]
    // An inline connection string: the ad-hoc path accepts whole references only.
    [InlineData("""{"reference":"Server=prod;Database=dw;Trusted_Connection=True","operation":"listObjects"}""")]
    // A hybrid reference carrying literal fragments.
    [InlineData("""{"reference":"${env:HOST};Extra=1","operation":"listObjects"}""")]
    // detectUniqueKey on a provider whose profiling T-SQL cannot run.
    [InlineData("""{"reference":"${env:X}","operation":"detectUniqueKey","kind":"MySQL","schema":"dbo","objectName":"t"}""")]
    // An unknown provider kind.
    [InlineData("""{"reference":"${env:X}","operation":"listObjects","kind":"DB2"}""")]
    // searchObjects without a term.
    [InlineData("""{"reference":"${env:X}","operation":"searchObjects"}""")]
    // A limit past the bound.
    [InlineData("""{"reference":"${env:X}","operation":"listObjects","limit":5000}""")]
    public async Task TriggerTask_WithInvalidBody_Returns400(string body)
    {
        // Every rejection here runs before any catalog query, so no database is needed.
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["operate"]);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/datasources/tasks", UriKind.Relative))
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task TriggerTask_ForAReferenceNoPipelineDeclares_Returns404()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["operate"]);

        using var response = await PostTaskAsync(client, token, new
        {
            reference = "${env:NEVER_DECLARED_" + Guid.NewGuid().ToString("N")[..8] + "}",
            operation = "listObjects",
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Datasources_ListsTheEstateReferences_WithKindAndUsage()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = FlowIdentity.FromName("cp_ds_" + suffix);
        var reference = "${env:CP_DS_" + suffix + "}";
        var now = DateTime.UtcNow;

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                // Two pipelines read the reference as their source; the definition of one carries the connection
                // object (connectionRef + kind), which is where the list reads the provider kind from.
                db.Pipelines.Add(SeedPipeline(repoId, "cp_ds_a_" + suffix, now, sourceServer: reference,
                    definitionJson: $$"""{"connections":[{"alias":"src","connectionRef":"{{reference}}","kind":"PostgreSQL"}]}"""));
                db.Pipelines.Add(SeedPipeline(repoId, "cp_ds_b_" + suffix, now, sourceServer: reference,
                    definitionJson: """{"name":"b"}"""));
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["read"]);

            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/datasources", UriKind.Relative));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var entry = doc.RootElement.EnumerateArray()
                .SingleOrDefault(d => d.GetProperty("reference").GetString() == reference);
            Assert.NotEqual(JsonValueKind.Undefined, entry.ValueKind);
            Assert.Equal("PostgreSQL", entry.GetProperty("kind").GetString());
            Assert.True(entry.GetProperty("resolvable").GetBoolean());
            Assert.Equal(2, entry.GetProperty("sourcePipelines").GetInt32());
            Assert.Equal(0, entry.GetProperty("targetPipelines").GetInt32());
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task TriggerTask_WithPool_StaysQueuedForThatPool_AndCancelDequeuesIt()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = FlowIdentity.FromName("cp_dsq_" + suffix);
        var reference = "${env:CP_DSQ_" + suffix + "}";
        var now = DateTime.UtcNow;

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Pipelines.Add(SeedPipeline(repoId, "cp_dsq_" + suffix, now, sourceServer: reference,
                    definitionJson: """{"name":"q"}"""));
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["operate"]);

            // Routed to a pool the in-process worker does not serve, so it stays deterministically queued.
            Guid taskId;
            using (var response = await PostTaskAsync(client, token, new
            {
                reference,
                operation = "listObjects",
                pool = "pool-" + suffix,
            }))
            {
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                var accepted = await response.Content.ReadFromJsonAsync<ComputeTaskAccepted>();
                Assert.NotNull(accepted);
                taskId = accepted.TaskId;
                Assert.Equal("queued", accepted.Status);
                Assert.Equal($"/api/v1/datasources/tasks/{taskId}", response.Headers.Location!.ToString());
            }

            // The detail endpoint reflects the queued task, its pool, and its audited requester payload fields.
            using (var get = AuthorizedGet(token, $"/api/v1/datasources/tasks/{taskId}"))
            {
                using var detail = await client.SendAsync(get);
                Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
                using var doc = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
                Assert.Equal("queued", doc.RootElement.GetProperty("status").GetString());
                Assert.Equal("pool-" + suffix, doc.RootElement.GetProperty("pool").GetString());
                Assert.Equal("listObjects", doc.RootElement.GetProperty("operation").GetString());
                Assert.Equal(reference, doc.RootElement.GetProperty("sourceRef").GetString());
            }

            // The task list surfaces it too, filtered by reference.
            using (var list = AuthorizedGet(token, $"/api/v1/datasources/tasks?reference={Uri.EscapeDataString(reference)}"))
            {
                using var listed = await client.SendAsync(list);
                Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
                using var doc = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
                Assert.Equal(1, doc.RootElement.GetProperty("total").GetInt64());
            }

            // Cancelling a queued task dequeues it outright: 200 with the terminal status.
            using (var cancel = new HttpRequestMessage(
                HttpMethod.Post, new Uri($"/api/v1/datasources/tasks/{taskId}/cancel", UriKind.Relative)))
            {
                cancel.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var cancelled = await client.SendAsync(cancel);
                Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
                var outcome = await cancelled.Content.ReadFromJsonAsync<ComputeTaskAccepted>();
                Assert.NotNull(outcome);
                Assert.Equal("cancelled", outcome.Status);
            }
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task TriggerTask_ExecutesOnTheWorker_AndReturnsTheLiveObjectListing()
    {
        // The full path: POST a listObjects task against the test catalog database itself (via a per-test
        // ${env:...} reference), let the in-process worker claim and execute it through the shared engine's
        // catalog reader, and read the live listing back from the task result. This proves trigger -> queue ->
        // worker -> provider -> result -> API end to end with zero secrets in any payload.
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = FlowIdentity.FromName("cp_dsx_" + suffix);
        var envName = "SQLFLOW_CP_DSX_" + suffix;
        var reference = "${env:" + envName + "}";
        var now = DateTime.UtcNow;

        Environment.SetEnvironmentVariable(envName, cs);
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Pipelines.Add(SeedPipeline(repoId, "cp_dsx_" + suffix, now, sourceServer: reference,
                    definitionJson: """{"name":"x"}"""));
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["operate"]);

            Guid taskId;
            using (var response = await PostTaskAsync(client, token, new
            {
                reference,
                operation = "listObjects",
                schema = "catalog",
                limit = 200,
            }))
            {
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                var accepted = await response.Content.ReadFromJsonAsync<ComputeTaskAccepted>();
                Assert.NotNull(accepted);
                taskId = accepted.TaskId;
            }

            // Long-poll the task to its terminal state (bounded so a stuck worker fails rather than hangs).
            string? status = null;
            string body = string.Empty;
            for (var attempt = 0; attempt < 60; attempt++)
            {
                using var get = AuthorizedGet(token, $"/api/v1/datasources/tasks/{taskId}?waitMs=2000");
                using var detail = await client.SendAsync(get);
                Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
                body = await detail.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                status = doc.RootElement.GetProperty("status").GetString();
                if (status is "succeeded" or "failed" or "cancelled")
                {
                    break;
                }
            }

            var workerLog = string.Join(Environment.NewLine, factory.Logs.Where(l =>
                l.Contains("Compute task", StringComparison.Ordinal)));
            Assert.True(status == "succeeded", $"the compute task ended '{status}'. body={body}{Environment.NewLine}Worker log:{Environment.NewLine}{workerLog}");

            // The result is the live listing of the catalog schema: its own tables are in it.
            using var result = JsonDocument.Parse(body);
            var items = result.RootElement.GetProperty("result").GetProperty("items").EnumerateArray().ToList();
            Assert.NotEmpty(items);
            Assert.Contains(items, i => i.GetProperty("name").GetString() == "ComputeTask");
            Assert.Contains(items, i => i.GetProperty("name").GetString() == "Run");
            Assert.All(items, i => Assert.Equal("catalog", i.GetProperty("schema").GetString()));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
            await CleanupAsync(cs, repoId);
        }
    }

    [SkippableTheory]
    [Trait("Category", "Integration")]
    // The docker/ compose sources, each seeded with Sakila (see docker/README.md). Every row skips unless its
    // connection env var is set, mirroring the ForeignSourceIngestionTests gating.
    [InlineData("PostgreSQL", "SQLFLOW_TEST_PG", null, "public", "actor")]
    [InlineData("MySQL", "SQLFLOW_TEST_MYSQL", "sakila", null, "actor")]
    [InlineData("Oracle", "SQLFLOW_TEST_ORACLE", null, "SAKILA", "ACTOR")]
    public async Task TriggerTask_BrowsesForeignEngines_EndToEnd(
        string kind, string envVar, string? database, string? schema, string expectedTable)
    {
        // The provider sanity check: the SAME compute path proven for SQL Server (trigger -> queue -> worker ->
        // provider catalog reader -> result) against a live PostgreSQL / MySQL / Oracle source. listObjects must
        // surface a known Sakila table, and introspectObject must return its live columns, proving the
        // per-provider reader dispatch works end to end and not just for the built-in provider.
        Skip.If(
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envVar)),
            $"Set {envVar} (see docker/README.md) to run the {kind} browse sanity check.");
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = FlowIdentity.FromName("cp_dsf_" + suffix);
        var reference = "${env:" + envVar + "}";
        var now = DateTime.UtcNow;

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Pipelines.Add(SeedPipeline(repoId, "cp_dsf_" + suffix, now, sourceServer: reference,
                    definitionJson: """{"name":"foreign"}"""));
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["operate"]);

            // 1. The listing: the known Sakila table must be in it.
            var listing = await RunTaskToCompletionAsync(client, token, new
            {
                reference,
                operation = "listObjects",
                kind,
                database,
                schema,
                limit = 200,
            });
            var items = listing.GetProperty("result").GetProperty("items").EnumerateArray().ToList();
            Assert.NotEmpty(items);
            var actor = items.SingleOrDefault(i => string.Equals(
                i.GetProperty("name").GetString(), expectedTable, StringComparison.OrdinalIgnoreCase));
            Assert.NotEqual(JsonValueKind.Undefined, actor.ValueKind);

            // 2. Live introspection of that table: Sakila's actor table has its well-known columns everywhere.
            var introspection = await RunTaskToCompletionAsync(client, token, new
            {
                reference,
                operation = "introspectObject",
                kind,
                database,
                schema = actor.GetProperty("schema").GetString(),
                objectName = actor.GetProperty("name").GetString(),
            });
            var result = introspection.GetProperty("result");
            Assert.True(result.GetProperty("found").GetBoolean());
            var columns = result.GetProperty("object").GetProperty("columns").EnumerateArray()
                .Select(c => c.GetProperty("name").GetString()!)
                .ToList();
            Assert.Contains(columns, c => string.Equals(c, "actor_id", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(columns, c => string.Equals(c, "last_name", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    /// <summary>Triggers one compute task and long-polls it to its terminal state, asserting it succeeded, and
    /// returns the terminal task document (including <c>result</c>).</summary>
    private static async Task<JsonElement> RunTaskToCompletionAsync(HttpClient client, string token, object request)
    {
        Guid taskId;
        using (var response = await PostTaskAsync(client, token, request))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var accepted = await response.Content.ReadFromJsonAsync<ComputeTaskAccepted>();
            Assert.NotNull(accepted);
            taskId = accepted.TaskId;
        }

        string? status = null;
        var body = string.Empty;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            using var get = AuthorizedGet(token, $"/api/v1/datasources/tasks/{taskId}?waitMs=2000");
            using var detail = await client.SendAsync(get);
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            body = await detail.Content.ReadAsStringAsync();
            using (var doc = JsonDocument.Parse(body))
            {
                status = doc.RootElement.GetProperty("status").GetString();
            }

            if (status is "succeeded" or "failed" or "cancelled")
            {
                break;
            }
        }

        Assert.True(status == "succeeded", $"the compute task ended '{status}'. body={body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task GetTask_ForUnknownTask_Returns404()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read"]);

        using var get = AuthorizedGet(token, $"/api/v1/datasources/tasks/{Guid.NewGuid()}");
        using var response = await client.SendAsync(get);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    private static CatalogPipeline SeedPipeline(
        Guid repoId, string name, DateTime now, string sourceServer, string definitionJson) => new()
    {
        Id = CatalogIdentity.Pipeline(repoId, name),
        RepoId = repoId,
        Name = name,
        Kind = "ing",
        RelativePath = "flows/" + name + ".flow.yaml",
        ContentHash = "0000000000000000000000000000000000000000000000000000000000000000",
        Yaml = $"name: {name}\n",
        DefinitionJson = definitionJson,
        SourceServer = sourceServer,
        Active = true,
        Wave = 0,
        FirstSeenUtc = now,
        LastSeenUtc = now,
    };

    private static async Task CleanupAsync(string cs, Guid repoId)
    {
        await using var db = CatalogDatabase.Create(cs);
        var refs = await db.Pipelines.Where(p => p.RepoId == repoId).Select(p => p.SourceServer).ToListAsync();
        await db.ComputeTasks.Where(t => refs.Contains(t.SourceRef)).ExecuteDeleteAsync();
        await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
    }

    private static HttpRequestMessage AuthorizedGet(string token, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static Task<HttpResponseMessage> PostTaskAsync(HttpClient client, string token, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/datasources/tasks", UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private static async Task<string> IssueTokenAsync(HttpClient client, IReadOnlyList<string> scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        Assert.False(string.IsNullOrWhiteSpace(token.AccessToken));
        return token.AccessToken;
    }
}
