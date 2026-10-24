using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The catalog browse end to end against the real shadow catalog: a database and a schema spelled two ways that differ
/// only in case are one grouping, shown in the spelling most of its objects carry, in the lineage schema groupings and in
/// the semantic layer's coverage alike; the object lists filter by the same rule, so a grouping's objects are exactly the
/// ones it counted; and an object whose database or schema a sync could not work out is asked for as such. Every seeded
/// row lives under a server reference and a database name unique to the test, and is removed in a finally.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CatalogBrowseApiTests
{
    [SkippableFact]
    public async Task Browse_FoldsSpellings_AndListsExactlyWhatEachGroupingCounted()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var serverRef = "${env:SQLFLOW_TEST_BROWSE_" + suffix.ToUpperInvariant() + "}";
        var database = "Warehouse_" + suffix;
        var lower = database.ToLowerInvariant();
        var now = DateTime.UtcNow;

        CatalogObject Seed(string? db, string? schema, string name, string kind) => new()
        {
            Key = SqlFlow.Lineage.Collection.NodeKey.For(serverRef, db, schema, name),
            ServerRef = serverRef,
            Database = db,
            Schema = schema,
            Name = name,
            Kind = kind,
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };

        CatalogObject[] seeded =
        [
            Seed(database, "dbo", "Orders", "Table"),
            Seed(lower, "DBO", "Customers", "Table"),
            Seed(database, "dbo", "vOrders", "View"),
            Seed(database, null, "Loose", "Table"),
            Seed(null, "pre", "Landed", "Table"),
        ];

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Objects.AddRange(seeded);
                // The semantic layer covers the objects with catalogued columns: the dbo tables and view, one of them spelled
                // the other way.
                foreach (var covered in seeded.Take(3))
                {
                    db.ObjectColumns.Add(new CatalogObjectColumn { ObjectKey = covered.Key, Ordinal = 1, Name = "Id", DataType = "int", Tier = "Observed" });
                }

                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await AiContextTestKit.IssueTokenAsync(client, ["read", "operate", "admin"]);
            var server = Uri.EscapeDataString(serverRef);

            // One database, one dbo schema, in the spelling most of their objects carry.
            var kinds = await GetJsonAsync<IReadOnlyList<SchemaKindCountDto>>(client, token, $"/api/v1/lineage/schemas/kinds?serverRef={server}");
            (string?, string?, string, int)[] expected =
                [(database, "dbo", "Table", 2), (database, "dbo", "View", 1), (database, null, "Table", 1), (null, "pre", "Table", 1)];
            Assert.Equal(
                expected,
                kinds.Select(k => (k.Database, k.Schema, k.Kind, k.ObjectCount))
                    .OrderBy(k => k.Database is null).ThenBy(k => k.Schema is null).ThenBy(k => k.Kind, StringComparer.Ordinal));
            Assert.All(kinds, k => Assert.Equal(serverRef, k.ServerRef));

            var schemas = await GetJsonAsync<IReadOnlyList<SchemaDto>>(client, token, $"/api/v1/lineage/schemas?serverRef={server}");
            Assert.Equal(3, schemas.Single(s => s.Database == database && s.Schema == "dbo").ObjectCount);

            // The objects of a grouping are exactly the ones it counted, whichever spelling asks for them.
            var tables = await GetJsonAsync<PagedResult<ObjectDto>>(
                client, token, $"/api/v1/lineage/objects?serverRef={server}&database={lower}&schema=DBO&kind=Table");
            Assert.Equal(["Customers", "Orders"], tables.Items.Select(o => o.Name).Order(StringComparer.Ordinal));

            var landed = await GetJsonAsync<PagedResult<ObjectDto>>(client, token, $"/api/v1/lineage/objects?serverRef={server}&databaseUnknown=true");
            Assert.Equal("Landed", Assert.Single(landed.Items).Name);

            var loose = await GetJsonAsync<PagedResult<ObjectDto>>(
                client, token, $"/api/v1/lineage/objects?serverRef={server}&database={database}&schemaUnknown=true");
            Assert.Equal("Loose", Assert.Single(loose.Items).Name);

            // Naming a database and asking for an unknown one at once is refused.
            using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/v1/lineage/objects?database={database}&databaseUnknown=true", UriKind.Relative)))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }

            // The semantic layer's coverage folds the same way: one row for the database's dbo, all three objects in it.
            var coverage = await GetJsonAsync<IReadOnlyList<SemanticSchemaCoverageDto>>(client, token, "/api/v1/powerai/semantic-layer/schemas");
            var row = Assert.Single(coverage, c => string.Equals(c.Database, database, StringComparison.OrdinalIgnoreCase));
            Assert.Equal((database, "dbo", 3), (row.Database, row.Schema, row.ObjectCount));

            var listed = await GetJsonAsync<PagedResult<SemanticObjectCoverageDto>>(
                client, token, $"/api/v1/powerai/semantic-layer/objects?database={lower}&schema=DBO");
            Assert.Equal(3, listed.Total);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            var keys = seeded.Select(o => o.Key).ToList();
            await db.ObjectColumns.Where(c => keys.Contains(c.ObjectKey)).ExecuteDeleteAsync();
            await db.Objects.Where(o => keys.Contains(o.Key)).ExecuteDeleteAsync();
        }
    }

    private static async Task<T> GetJsonAsync<T>(HttpClient client, string token, string relativeUri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(relativeUri, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(value);
        return value;
    }
}
