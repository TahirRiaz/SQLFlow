using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The join-path search end to end against the real shadow catalog: "how can I join this table, and what are
/// the alternatives". The seeded graph is the shape a warehouse actually has, a fact joined to two dimensions
/// with one dimension reachable only through the other, plus a second relationship between the same pair of
/// tables on a DIFFERENT column set, which is the ambiguity the search must surface rather than resolve.
/// Every seeded row is removed in a finally so repeated runs stay isolated in a shared catalog.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LineageJoinPathApiTests
{
    [SkippableFact]
    public async Task JoinPaths_RankBestFirst_KeepRivalRoutes_AndReachThroughABridge()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var serverRef = "${env:SQLFLOW_JOIN_" + suffix + "}";
        var repoId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var fact = $"{serverRef}|dw|edw|ferrypassengers_{suffix}";
        var route = $"{serverRef}|dw|edw|dim_route_{suffix}";
        var operatorDim = $"{serverRef}|dw|edw|dim_operator_{suffix}";
        var stranded = $"{serverRef}|dw|edw|stranded_{suffix}";

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Repos.Add(new CatalogRepo
                {
                    Id = repoId, Name = "joins_" + suffix, FirstSeenUtc = now, LastSyncUtc = now,
                });

                db.Objects.Add(SeedTable(fact, serverRef, "DW", "edw", $"FerryPassengers_{suffix}", now));
                db.Objects.Add(SeedTable(route, serverRef, "DW", "edw", $"Dim_Route_{suffix}", now));
                db.Objects.Add(SeedTable(operatorDim, serverRef, "DW", "edw", $"Dim_Operator_{suffix}", now));
                db.Objects.Add(SeedTable(stranded, serverRef, "DW", "edw", $"Stranded_{suffix}", now));

                // The canonical route join, used widely, and a rival join between the SAME two tables on a
                // different column set that only one script uses. Both must survive.
                db.ObjectRelationships.Add(
                    Relationship(repoId, fact, "RouteId", route, "RouteId", "Join", occurrences: 12));
                db.ObjectRelationships.Add(
                    Relationship(repoId, fact, "RouteNumber", route, "RouteNumber", "Join", occurrences: 1));

                // The operator dimension hangs off the route dimension, so it is reachable from the fact only
                // through a bridge. Declared as a constraint, which must outrank an inferred join of equal
                // hop count and support.
                db.ObjectRelationships.Add(
                    Relationship(repoId, route, "OperatorId", operatorDim, "OperatorId", "Constraint",
                        occurrences: 1, name: "FK_Route_Operator"));

                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueReadTokenAsync(client);

            // 1. "How can I join the fact table?" One hop reaches only the route dimension, but by TWO distinct
            //    column sets, and both are offered rather than one being chosen for the caller.
            var direct = await GetJsonAsync<JoinPathsDto>(
                client, token, $"/api/v1/lineage/objects/join-paths?key={Uri.EscapeDataString(fact)}&maxHops=1");

            Assert.Equal(2, direct.PathCount);
            Assert.All(direct.Paths, p => Assert.True(p.IsDirect));
            Assert.All(direct.Paths, p => Assert.Equal(route, p.TargetObjectKey));

            // Best first: the widely used join leads, and it renders a pasteable ON clause.
            var best = direct.Paths[0];
            Assert.Equal(12, best.MinOccurrences);
            Assert.Contains("RouteId", best.Hops.Single().On, StringComparison.Ordinal);
            Assert.Contains(" = ", best.Hops.Single().On, StringComparison.Ordinal);
            Assert.Equal(1, direct.Paths[1].MinOccurrences);

            // 2. The operator dimension is NOT reachable in one hop.
            var tooShallow = await GetJsonAsync<JoinPathsDto>(
                client, token,
                $"/api/v1/lineage/objects/join-paths?key={Uri.EscapeDataString(fact)}&maxHops=1&target=Dim_Operator_{suffix}");
            Assert.Equal(0, tooShallow.PathCount);
            Assert.Contains("maxHops", tooShallow.Note, StringComparison.Ordinal);

            // 3. Two hops finds it through the route dimension, and the chain reads in order.
            var bridged = await GetJsonAsync<JoinPathsDto>(
                client, token,
                $"/api/v1/lineage/objects/join-paths?key={Uri.EscapeDataString(fact)}&maxHops=2&target=Dim_Operator_{suffix}");

            Assert.True(bridged.PathCount >= 1);
            var chain = bridged.Paths[0];
            Assert.Equal(2, chain.HopCount);
            Assert.False(chain.IsDirect);
            Assert.Equal(fact, chain.Hops[0].FromObjectKey);
            Assert.Equal(route, chain.Hops[0].ToObjectKey);
            Assert.Equal(route, chain.Hops[1].FromObjectKey);
            Assert.Equal(operatorDim, chain.Hops[1].ToObjectKey);
            Assert.Equal("Constraint", chain.Hops[1].Origin);

            // The weakest link governs the ranking: the second hop is used once, so the whole route scores one.
            Assert.Equal(1, chain.MinOccurrences);

            // 4. A table nothing joins reports no route, and says so rather than returning a bare empty list
            //    that a reader could mistake for "no relationship exists".
            var none = await GetJsonAsync<JoinPathsDto>(
                client, token, $"/api/v1/lineage/objects/join-paths?key={Uri.EscapeDataString(stranded)}");
            Assert.Equal(0, none.PathCount);
            Assert.Contains("No relationship is recorded", none.Note, StringComparison.Ordinal);

            // 5. An unknown object is a 404, not an empty answer that reads as "nothing joins it".
            using var missing = await SendAsync(
                client, token, "/api/v1/lineage/objects/join-paths?key=nope|nope|nope|nope");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.ObjectRelationships.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.Objects.Where(o => o.ServerRef == serverRef).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    private static CatalogObjectRelationship Relationship(
        Guid repoId, string fromKey, string fromColumns, string toKey, string toColumns,
        string origin, int occurrences, string? name = null)
        => new()
        {
            RepoId = repoId,
            Name = name,
            FromObjectKey = fromKey,
            FromColumns = fromColumns,
            ToObjectKey = toKey,
            ToColumns = toColumns,
            Origin = origin,
            Tier = origin == "Constraint" ? "Declared" : "Observed",
            Occurrences = occurrences,
        };

    private static CatalogObject SeedTable(
        string key, string serverRef, string database, string schema, string name, DateTime now)
        => new()
        {
            Key = key,
            ServerRef = serverRef,
            Database = database,
            Schema = schema,
            Name = name,
            Kind = "Table",
            FirstSeenUtc = now,
            LastSeenUtc = now,
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

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, string relativeUri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(relativeUri, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<T> GetJsonAsync<T>(HttpClient client, string token, string relativeUri)
    {
        using var response = await SendAsync(client, token, relativeUri);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(value);
        return value;
    }
}
