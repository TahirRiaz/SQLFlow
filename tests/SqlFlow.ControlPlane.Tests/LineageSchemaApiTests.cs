using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The schema-browse and object-dossier read API end to end against the real shadow catalog: the test seeds a
/// handful of catalog objects (with a script and a column dictionary) under one isolated server identity, then
/// asserts that <c>/lineage/schemas</c> reports the (server, database, schema) groupings with counts, that
/// <c>/lineage/objects</c> filters by database and schema, and that <c>/lineage/objects/dossier</c> returns an
/// object's script and columns. This is the "answer questions about the schema" path a model uses. Every
/// seeded row is removed in a finally so repeated runs stay isolated.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LineageSchemaApiTests
{
    [SkippableFact]
    public async Task SchemaBrowse_ListsSchemas_FiltersObjects_AndReturnsDossier()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        // An isolated server identity so the schema grouping and counts are deterministic in a shared catalog.
        var serverRef = "${env:SQLFLOW_TEST_" + suffix + "}";
        var ordersKey = $"{serverRef}|dw|dbo|orders";
        var repoId = FlowIdentity.FromName("lineage_edges_" + suffix);
        var now = DateTime.UtcNow;

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                var orders = SeedObject(ordersKey, serverRef, "DW", "dbo", "Orders", "Table", now,
                    script: "CREATE TABLE [DW].[dbo].[Orders] ([Id] int NOT NULL, [Amount] decimal(18,2) NULL);");
                orders.KeyColumns = "Id";
                orders.KeyOrigin = "Declared";
                db.Objects.Add(orders);
                db.Objects.Add(SeedObject($"{serverRef}|dw|dbo|vorders", serverRef, "DW", "dbo", "vOrders", "View", now));
                db.Objects.Add(SeedObject($"{serverRef}|dw|stg|orders", serverRef, "DW", "stg", "Orders", "Table", now));

                // The same interpreted relationship observed from two repos: the dossier must fold them into
                // one row keeping the highest occurrence count, oriented from the staging table onto Orders.
                db.ObjectRelationships.Add(SeedRelationship(repoId, $"{serverRef}|dw|stg|orders", ordersKey, 3));
                db.ObjectRelationships.Add(SeedRelationship(FlowIdentity.FromName("other_" + suffix), $"{serverRef}|dw|stg|orders", ordersKey, 2));

                db.ObjectColumns.Add(new CatalogObjectColumn { ObjectKey = ordersKey, Ordinal = 1, Name = "Id", DataType = "int", Nullable = false, Tier = "Observed" });
                db.ObjectColumns.Add(new CatalogObjectColumn { ObjectKey = ordersKey, Ordinal = 2, Name = "Amount", DataType = "decimal(18,2)", Nullable = true, Tier = "Observed" });

                // A repo with one edge onto the seeded object, and one onto an object the registry does not
                // know, so the edge list's registry join is proven for both the hit and the miss.
                db.Repos.Add(new CatalogRepo { Id = repoId, Name = "lineage_edges_" + suffix, FirstSeenUtc = now, LastSyncUtc = now });
                db.LineageEdges.Add(new CatalogLineageEdge
                {
                    RepoId = repoId, Flow = "flow_" + suffix, Relation = "Writes",
                    ObjectKey = ordersKey, ObjectName = "Orders", Tier = "Declared",
                });
                db.LineageEdges.Add(new CatalogLineageEdge
                {
                    RepoId = repoId, Flow = "flow_" + suffix, Relation = "Reads",
                    ObjectKey = $"{serverRef}|dw|dbo|unregistered", ObjectName = "Unregistered", Tier = "Declared",
                });
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueReadTokenAsync(client);

            // The schema hierarchy: two schemas under DW on our server, dbo with 2 objects and stg with 1.
            var schemas = await GetJsonAsync<IReadOnlyList<SchemaDto>>(
                client, token, $"/api/v1/lineage/schemas?serverRef={Uri.EscapeDataString(serverRef)}");
            Assert.Equal(2, schemas.Count);
            Assert.Equal(2, schemas.Single(s => s.Schema == "dbo").ObjectCount);
            Assert.Equal(1, schemas.Single(s => s.Schema == "stg").ObjectCount);
            Assert.All(schemas, s => Assert.Equal("DW", s.Database));

            // The per-kind breakdown: dbo splits into 1 Table + 1 View, stg is 1 Table, and the counts per
            // (schema, kind) group sum to the schema totals above.
            var kinds = await GetJsonAsync<IReadOnlyList<SchemaKindCountDto>>(
                client, token, $"/api/v1/lineage/schemas/kinds?serverRef={Uri.EscapeDataString(serverRef)}");
            Assert.Equal(3, kinds.Count);
            Assert.Equal(1, kinds.Single(k => k.Schema == "dbo" && k.Kind == "Table").ObjectCount);
            Assert.Equal(1, kinds.Single(k => k.Schema == "dbo" && k.Kind == "View").ObjectCount);
            Assert.Equal(1, kinds.Single(k => k.Schema == "stg" && k.Kind == "Table").ObjectCount);
            Assert.Equal(schemas.Sum(s => s.ObjectCount), kinds.Sum(k => k.ObjectCount));

            // The schema filter narrows the breakdown to one schema's kinds.
            var dboKinds = await GetJsonAsync<IReadOnlyList<SchemaKindCountDto>>(
                client, token, $"/api/v1/lineage/schemas/kinds?serverRef={Uri.EscapeDataString(serverRef)}&schema=dbo");
            Assert.Equal(2, dboKinds.Count);
            Assert.All(dboKinds, k => Assert.Equal("dbo", k.Schema));

            // Filtering objects by database + schema enumerates just that schema's objects.
            var dboObjects = await GetJsonAsync<PagedResult<ObjectDto>>(
                client, token, $"/api/v1/lineage/objects?serverRef={Uri.EscapeDataString(serverRef)}&database=DW&schema=dbo&pageSize=200");
            Assert.Equal(2, dboObjects.Total);
            Assert.All(dboObjects.Items, o => Assert.Equal("dbo", o.Schema));

            // The dossier returns the object's generating script and its column dictionary in one payload.
            var dossier = await GetJsonAsync<ObjectDossierDto>(
                client, token, $"/api/v1/lineage/objects/dossier?key={Uri.EscapeDataString(ordersKey)}");
            Assert.Equal(ordersKey, dossier.Object.Key);
            Assert.Contains("CREATE TABLE", dossier.Object.Script ?? string.Empty, StringComparison.Ordinal);
            Assert.Equal(2, dossier.Columns.Count);
            Assert.Equal(["Amount", "Id"], dossier.Columns.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal));

            // The interpreted data model rides the dossier: the object's key, and the relationship rows the
            // two repos observed folded into one incoming reference (the staging table joins onto Orders).
            Assert.Equal("Id", dossier.Object.KeyColumns);
            Assert.Equal("Declared", dossier.Object.KeyOrigin);
            Assert.Empty(dossier.References);
            var incoming = Assert.Single(dossier.ReferencedBy);
            Assert.Equal($"{serverRef}|dw|stg|orders", incoming.OtherObjectKey);
            Assert.Equal("Orders", incoming.OtherName);
            Assert.Equal("stg", incoming.OtherSchema);
            Assert.Equal("Id", incoming.OwnColumns);
            Assert.Equal("OrderId", incoming.OtherColumns);
            Assert.Equal("Join", incoming.Origin);
            Assert.Equal(3, incoming.Occurrences);

            // The edge list joins the global registry for each object's proper-cased database/schema; an edge
            // whose object is not registered still returns, with nulls.
            var edges = await GetJsonAsync<PagedResult<EdgeDto>>(
                client, token, $"/api/v1/repos/{repoId}/lineage/edges?pageSize=50");
            Assert.Equal(2, edges.Total);
            var registered = edges.Items.Single(e => e.ObjectKey == ordersKey);
            Assert.Equal("DW", registered.ObjectDatabase);
            Assert.Equal("dbo", registered.ObjectSchema);
            var unregistered = edges.Items.Single(e => e.ObjectKey != ordersKey);
            Assert.Null(unregistered.ObjectDatabase);
            Assert.Null(unregistered.ObjectSchema);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.ObjectColumns.Where(c => c.ObjectKey.StartsWith(serverRef)).ExecuteDeleteAsync();
            await db.ObjectRelationships.Where(r => r.ToObjectKey.StartsWith(serverRef)).ExecuteDeleteAsync();
            await db.Objects.Where(o => o.ServerRef == serverRef).ExecuteDeleteAsync();
            await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    private static CatalogObject SeedObject(
        string key, string serverRef, string database, string schema, string name, string kind, DateTime now, string? script = null)
        => new()
        {
            Key = key,
            ServerRef = serverRef,
            Database = database,
            Schema = schema,
            Name = name,
            Kind = kind,
            Script = script,
            ScriptTier = script is null ? null : "Observed",
            ScriptUpdatedUtc = script is null ? null : now,
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };

    private static CatalogObjectRelationship SeedRelationship(Guid repoId, string fromKey, string toKey, int occurrences)
        => new()
        {
            RepoId = repoId,
            FromObjectKey = fromKey,
            FromColumns = "OrderId",
            ToObjectKey = toKey,
            ToColumns = "Id",
            Origin = "Join",
            Tier = "Derived",
            Occurrences = occurrences,
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
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(relativeUri, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(value);
        return value;
    }
}
