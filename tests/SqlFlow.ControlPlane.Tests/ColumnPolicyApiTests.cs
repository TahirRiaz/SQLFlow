using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The "AI shouldn't be able to read everything" surface end to end, under the default-deny allow-list: no column's
/// data is open until an admin approves an AI context naming its table, and then every column of it is open but those
/// its file denies. Until then the DataOps prepare step refuses a query reading it (including through a <c>SELECT *</c>
/// that would otherwise smuggle it through unnamed). Schema is not sensitive, so schema search, the object's column
/// list and its dossier show every column all along. A file that stops denying a column changes nothing until it is
/// approved again, and a file that denies one closes it at the next sync, with no approval. Every seeded row is
/// removed in a finally so repeated runs stay isolated.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ColumnPolicyApiTests
{
    [SkippableFact]
    public async Task AColumnNoApprovedContextOpens_IsListedByEverySchemaSurface_ButRefusesQueriesThatWouldReadIt()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var serverRef = "${env:SQLFLOW_COLPOL_" + suffix + "}";
        var objectKey = $"{serverRef}|dw|dbo|employee_{suffix}";
        var tableName = "Employee_" + suffix;
        var repoId = Guid.NewGuid();
        var pipelineId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs)
            .WithSetting("ControlPlane:AiContext:Enabled", "true");
        await using var contexts = new AiContextTestKit(cs, repoId);
        string ContextFile(params string[] denied)
            => $"aiContext: people-{suffix}\ntables:\n  - table: dw.dbo.{tableName}\n"
                + (denied.Length == 0 ? string.Empty : $"    deny: [{string.Join(", ", denied)}]\n");
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Objects.Add(new CatalogObject
                {
                    Key = objectKey, ServerRef = serverRef, Database = "dw", Schema = "dbo", Name = tableName,
                    Kind = "Table", FirstSeenUtc = now, LastSeenUtc = now,
                });
                db.ObjectColumns.Add(new CatalogObjectColumn
                {
                    ObjectKey = objectKey, Ordinal = 1, Name = "Ssn", DataType = "varchar(11)", Nullable = false, Tier = "Observed",
                });
                db.ObjectColumns.Add(new CatalogObjectColumn
                {
                    ObjectKey = objectKey, Ordinal = 2, Name = "Name", DataType = "nvarchar(100)", Nullable = false, Tier = "Observed",
                });

                db.Repos.Add(new CatalogRepo { Id = repoId, Name = "colpol_" + suffix, FirstSeenUtc = now, LastSyncUtc = now });
                db.Pipelines.Add(new CatalogPipeline
                {
                    Id = pipelineId, RepoId = repoId, Name = "colpol_flow_" + suffix, Kind = "ing",
                    RelativePath = "colpol/flow.yaml", Active = true, SourceServer = serverRef, TargetServer = serverRef,
                    DefinitionJson = "{}", FirstSeenUtc = now, LastSeenUtc = now,
                });
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var adminToken = await IssueTokenAsync(client, ["read", "operate", "admin"]);

            // 1. Before any context is approved, both columns are closed: any query touching either is refused, while
            //    schema search finds both, since schema is not sensitive.
            var beforeColumns = await GetJsonAsync<PagedResult<ColumnHitDto>>(
                client, adminToken, $"/api/v1/search/columns?name={Uri.EscapeDataString("Ssn")}");
            Assert.Contains(beforeColumns.Items, c => c.ObjectKey == objectKey && c.ColumnName == "Ssn");

            using var beforeRefusal = await PostAsync(client, adminToken, "/api/v1/dataops/queries/prepare",
                new PrepareQueryRequest($"SELECT Name FROM dbo.{tableName}", serverRef, Database: "dw"));
            Assert.Equal(HttpStatusCode.BadRequest, beforeRefusal.StatusCode);

            // 2. A file opens the table but denies "Ssn". Synced, it is a proposal: nothing opens until an admin approves it.
            contexts.Write("people.ai-context.yaml", ContextFile("Ssn"));
            await contexts.SyncAsync();
            Assert.Empty(await contexts.AllowedColumnsAsync(objectKey));
            using (var stillRefused = await PostAsync(client, adminToken, "/api/v1/dataops/queries/prepare",
                new PrepareQueryRequest($"SELECT Name FROM dbo.{tableName}", serverRef, Database: "dw")))
            {
                Assert.Equal(HttpStatusCode.BadRequest, stillRefused.StatusCode);
            }

            await AiContextTestKit.ApproveAsync(client, adminToken, $"people-{suffix}");

            // 3. The admin state view shows Name allowed and Ssn still not.
            var state = await GetJsonAsync<IReadOnlyList<ColumnPolicyStateDto>>(
                client, adminToken, $"/api/v1/powerai/column-policies/objects/{Uri.EscapeDataString(objectKey)}");
            Assert.True(state.Single(c => c.ColumnName == "Name").IsAllowed);
            Assert.Equal($"people-{suffix}", state.Single(c => c.ColumnName == "Name").Context);
            Assert.False(state.Single(c => c.ColumnName == "Ssn").IsAllowed);
            Assert.Null(state.Single(c => c.ColumnName == "Ssn").Context);

            // 4. The catalog-wide "not allowed" overview still lists Ssn (never reviewed, no policy row at all).
            var overview = await GetJsonAsync<PagedResult<RestrictedColumnDto>>(
                client, adminToken, "/api/v1/powerai/column-policies?pageSize=200");
            Assert.Contains(overview.Items, c => c.ObjectKey == objectKey && c.ColumnName == "Ssn");
            Assert.DoesNotContain(overview.Items, c => c.ObjectKey == objectKey && c.ColumnName == "Name");

            // 5. The schema surfaces list both columns whatever is open: schema search, and the object's paged column
            //    list and dossier.
            var ssnHits = await GetJsonAsync<PagedResult<ColumnHitDto>>(
                client, adminToken, $"/api/v1/search/columns?name={Uri.EscapeDataString("Ssn")}");
            Assert.Contains(ssnHits.Items, c => c.ObjectKey == objectKey && c.ColumnName == "Ssn");

            var pagedColumns = await GetJsonAsync<PagedResult<ObjectColumnDto>>(
                client, adminToken, $"/api/v1/lineage/objects/columns?key={Uri.EscapeDataString(objectKey)}");
            Assert.Equal(["Ssn", "Name"], pagedColumns.Items.Select(c => c.Name));

            var dossier = await GetJsonAsync<ObjectDossierDto>(
                client, adminToken, $"/api/v1/lineage/objects/dossier?key={Uri.EscapeDataString(objectKey)}");
            Assert.Equal(["Ssn", "Name"], dossier.Columns.Select(c => c.Name));

            // 6. A query naming the still-unallowed column directly is refused at prepare...
            using var directRefusal = await PostAsync(client, adminToken, "/api/v1/dataops/queries/prepare",
                new PrepareQueryRequest($"SELECT Ssn FROM dbo.{tableName}", serverRef, Database: "dw"));
            Assert.Equal(HttpStatusCode.BadRequest, directRefusal.StatusCode);

            // ...so is a SELECT * that would expose it unnamed...
            using var starRefusal = await PostAsync(client, adminToken, "/api/v1/dataops/queries/prepare",
                new PrepareQueryRequest($"SELECT * FROM dbo.{tableName}", serverRef, Database: "dw"));
            Assert.Equal(HttpStatusCode.BadRequest, starRefusal.StatusCode);

            // ...but a query that reads only the allowed column now prepares normally.
            using var allowed = await PostAsync(client, adminToken, "/api/v1/dataops/queries/prepare",
                new PrepareQueryRequest($"SELECT Name FROM dbo.{tableName}", serverRef, Database: "dw"));
            allowed.EnsureSuccessStatusCode();

            // 7. The file stops denying Ssn: a change waiting for approval, so Ssn stays closed and SELECT * stays refused...
            contexts.Write("people.ai-context.yaml", ContextFile());
            await contexts.SyncAsync();
            Assert.Equal(AiContextStatus.Changed, (await AiContextTestKit.GetAsync(client, adminToken, $"people-{suffix}")).Summary.Status);
            using (var notYet = await PostAsync(client, adminToken, "/api/v1/dataops/queries/prepare",
                new PrepareQueryRequest($"SELECT * FROM dbo.{tableName}", serverRef, Database: "dw")))
            {
                Assert.Equal(HttpStatusCode.BadRequest, notYet.StatusCode);
            }

            // ...until an admin approves it; then SELECT * prepares.
            await AiContextTestKit.ApproveAsync(client, adminToken, $"people-{suffix}");
            using var starNowAllowed = await PostAsync(client, adminToken, "/api/v1/dataops/queries/prepare",
                new PrepareQueryRequest($"SELECT * FROM dbo.{tableName}", serverRef, Database: "dw"));
            starNowAllowed.EnsureSuccessStatusCode();

            // 8. The file denies Ssn again: the next sync closes it at once, with no approval, while Name stays open.
            contexts.Write("people.ai-context.yaml", ContextFile("Ssn"));
            await contexts.SyncAsync();
            Assert.Equal(["Name"], await contexts.AllowedColumnsAsync(objectKey));
            using var deniedAgain = await PostAsync(client, adminToken, "/api/v1/dataops/queries/prepare",
                new PrepareQueryRequest($"SELECT Ssn FROM dbo.{tableName}", serverRef, Database: "dw"));
            Assert.Equal(HttpStatusCode.BadRequest, deniedAgain.StatusCode);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.QueryPlans.Where(p => p.SourceRef == serverRef).ExecuteDeleteAsync();
            await db.ColumnPolicies.Where(p => p.ObjectKey == objectKey).ExecuteDeleteAsync();
            await db.ObjectColumns.Where(c => c.ObjectKey == objectKey).ExecuteDeleteAsync();
            await db.Objects.Where(o => o.Key == objectKey).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// Closes the exact gap a column blacklist could not: an object the catalog has no record of at all - a
    /// synonym pointed at a sensitive table is the motivating case, but this covers any object type the
    /// harvester does not track - has no allow-list to check a query against, so it must be refused outright
    /// rather than let through unchecked.
    /// </summary>
    [SkippableFact]
    public async Task QueryAgainstAnObjectTheCatalogDoesNotKnow_IsRefused()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var serverRef = "${env:SQLFLOW_COLPOL_" + suffix + "}";
        var repoId = Guid.NewGuid();
        var pipelineId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var unknownTableName = "NoSuchSynonym_" + suffix;

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs)
            .WithSetting("ControlPlane:AiContext:Enabled", "true");
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Repos.Add(new CatalogRepo { Id = repoId, Name = "colpolunknown_" + suffix, FirstSeenUtc = now, LastSyncUtc = now });
                db.Pipelines.Add(new CatalogPipeline
                {
                    Id = pipelineId, RepoId = repoId, Name = "colpolunknown_flow_" + suffix, Kind = "ing",
                    RelativePath = "colpolunknown/flow.yaml", Active = true, SourceServer = serverRef, TargetServer = serverRef,
                    DefinitionJson = "{}", FirstSeenUtc = now, LastSeenUtc = now,
                });
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var adminToken = await IssueTokenAsync(client, ["read", "operate", "admin"]);

            // No CatalogObject was ever registered for this name (it deliberately does not exist in the
            // catalog, standing in for a synonym or any object type the harvester never tracks), so there is no
            // allow-list to check the query against - it must be refused, not passed through.
            using var refusal = await PostAsync(client, adminToken, "/api/v1/dataops/queries/prepare",
                new PrepareQueryRequest($"SELECT Name FROM dbo.{unknownTableName}", serverRef, Database: "dw"));
            Assert.Equal(HttpStatusCode.BadRequest, refusal.StatusCode);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.QueryPlans.Where(p => p.SourceRef == serverRef).ExecuteDeleteAsync();
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

    private static async Task<HttpResponseMessage> PostAsync<T>(HttpClient client, string token, string relativeUri, T body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(relativeUri, UriKind.Relative)) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
}
