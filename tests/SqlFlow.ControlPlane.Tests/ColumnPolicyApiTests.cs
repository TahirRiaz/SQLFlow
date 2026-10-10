using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The "AI shouldn't be able to read everything" surface end to end, under the default-deny allow-list: a
/// column with no policy row is hidden exactly like one explicitly denied, and an admin has to allow-list a
/// column via <c>/powerai/column-policies</c> before it appears in schema search, the object dossier/column
/// list, or is readable by the DataOps prepare step (including through a <c>SELECT *</c> that would otherwise
/// smuggle a not-yet-allowed column through unnamed). Every seeded row is removed in a finally so repeated runs
/// stay isolated.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ColumnPolicyApiTests
{
    [SkippableFact]
    public async Task UnallowedColumn_IsHiddenFromSchemaSurfaces_AndRefusesQueriesThatWouldReadIt()
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
            .WithSetting("ControlPlane:DataOps:Enabled", "true");
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

            // 1. Before any policy row exists, BOTH columns are new/unreviewed, so both default to denied:
            //    invisible to search, and any query touching either is refused.
            var beforeColumns = await GetJsonAsync<PagedResult<ColumnHitDto>>(
                client, adminToken, $"/api/v1/search/columns?name={Uri.EscapeDataString("Ssn")}");
            Assert.DoesNotContain(beforeColumns.Items, c => c.ObjectKey == objectKey);

            using var beforeRefusal = await PostAsync(client, adminToken, "/api/v1/dataops/queries/prepare",
                new PrepareQueryRequest($"SELECT Name FROM dbo.{tableName}", serverRef, Database: "dw"));
            Assert.Equal(HttpStatusCode.BadRequest, beforeRefusal.StatusCode);

            // 2. An admin allow-lists only "Name", leaving "Ssn" unreviewed/denied.
            using var setResponse = await PutAsync(client, adminToken, "/api/v1/powerai/column-policies",
                new SetColumnPolicyRequest(objectKey, "Name", IsAllowed: true, Reason: "not PII"));
            setResponse.EnsureSuccessStatusCode();
            var setResult = await setResponse.Content.ReadFromJsonAsync<ColumnPolicyStateDto>();
            Assert.NotNull(setResult);
            Assert.True(setResult.IsAllowed);
            Assert.Equal("not PII", setResult.Reason);

            // 3. The admin state view shows Name allowed and Ssn still not.
            var state = await GetJsonAsync<IReadOnlyList<ColumnPolicyStateDto>>(
                client, adminToken, $"/api/v1/powerai/column-policies/objects/{Uri.EscapeDataString(objectKey)}");
            Assert.True(state.Single(c => c.ColumnName == "Name").IsAllowed);
            Assert.False(state.Single(c => c.ColumnName == "Ssn").IsAllowed);

            // 4. The catalog-wide "not allowed" overview still lists Ssn (never reviewed, no policy row at all).
            var overview = await GetJsonAsync<PagedResult<RestrictedColumnDto>>(
                client, adminToken, "/api/v1/powerai/column-policies?pageSize=200");
            Assert.Contains(overview.Items, c => c.ObjectKey == objectKey && c.ColumnName == "Ssn");
            Assert.DoesNotContain(overview.Items, c => c.ObjectKey == objectKey && c.ColumnName == "Name");

            // 5. Name is now visible to schema search, Ssn still is not...
            var afterColumns = await GetJsonAsync<PagedResult<ColumnHitDto>>(
                client, adminToken, $"/api/v1/search/columns?name={Uri.EscapeDataString("Name")}");
            Assert.Contains(afterColumns.Items, c => c.ObjectKey == objectKey && c.ColumnName == "Name");
            var stillHiddenSsn = await GetJsonAsync<PagedResult<ColumnHitDto>>(
                client, adminToken, $"/api/v1/search/columns?name={Uri.EscapeDataString("Ssn")}");
            Assert.DoesNotContain(stillHiddenSsn.Items, c => c.ObjectKey == objectKey);

            // ...and the object's paged column list and dossier carry Name but not Ssn.
            var pagedColumns = await GetJsonAsync<PagedResult<ObjectColumnDto>>(
                client, adminToken, $"/api/v1/lineage/objects/columns?key={Uri.EscapeDataString(objectKey)}");
            Assert.Contains(pagedColumns.Items, c => c.Name == "Name");
            Assert.DoesNotContain(pagedColumns.Items, c => c.Name == "Ssn");

            var dossier = await GetJsonAsync<ObjectDossierDto>(
                client, adminToken, $"/api/v1/lineage/objects/dossier?key={Uri.EscapeDataString(objectKey)}");
            Assert.Contains(dossier.Columns, c => c.Name == "Name");
            Assert.DoesNotContain(dossier.Columns, c => c.Name == "Ssn");

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

            // 7. Allowing Ssn too, SELECT * now works; denying it again reverses just that column.
            using var allowSsn = await PutAsync(client, adminToken, "/api/v1/powerai/column-policies",
                new SetColumnPolicyRequest(objectKey, "Ssn", IsAllowed: true, Reason: null));
            allowSsn.EnsureSuccessStatusCode();

            using var starNowAllowed = await PostAsync(client, adminToken, "/api/v1/dataops/queries/prepare",
                new PrepareQueryRequest($"SELECT * FROM dbo.{tableName}", serverRef, Database: "dw"));
            starNowAllowed.EnsureSuccessStatusCode();

            using var denySsnAgain = await PutAsync(client, adminToken, "/api/v1/powerai/column-policies",
                new SetColumnPolicyRequest(objectKey, "Ssn", IsAllowed: false, Reason: "PII"));
            denySsnAgain.EnsureSuccessStatusCode();

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
            .WithSetting("ControlPlane:DataOps:Enabled", "true");
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

    private static async Task<HttpResponseMessage> PutAsync<T>(HttpClient client, string token, string relativeUri, T body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, new Uri(relativeUri, UriKind.Relative)) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
}
