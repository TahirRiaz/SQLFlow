using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The Schema changes page's databases: the route's grouping is translated to SQL, so only the real catalog can show it
/// works. Every seeded row sits under a repository id unique to the test, and is removed in a finally.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SchemaChangeDatabasesApiTests
{
    [SkippableFact]
    public async Task Databases_AreListedInOrder_WithWhatHappenedInEach()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var repo = Guid.NewGuid();
        var run = Guid.NewGuid();
        var latest = new DateTime(2026, 8, 23, 3, 30, 0, DateTimeKind.Utc);

        CatalogSchemaChange Seed(string database, string name, string change, DateTime occurred) => new()
        {
            RepoId = repo,
            RunId = run,
            Database = database,
            Category = "Table",
            Schema = "dbo",
            Name = name,
            ChangeType = change,
            OccurredUtc = occurred,
        };

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.SchemaChanges.AddRange(
                    Seed("warehouse", "Orders", SchemaChangeKinds.Added, latest.AddDays(-1)),
                    Seed("warehouse", "Customers", SchemaChangeKinds.Changed, latest),
                    Seed("warehouse", "Regions", SchemaChangeKinds.Changed, latest.AddDays(-2)),
                    Seed("archive", "Orders2019", SchemaChangeKinds.Deleted, latest.AddDays(-3)),
                    Seed("archive", "OldEvents", SchemaChangeKinds.Deleted, latest.AddDays(-30)));
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await AiContextTestKit.IssueTokenAsync(client, ["read"]);

            var all = await GetAsync(client, token, $"/api/v1/schema-changes/databases?repoId={repo}");
            Assert.Equal(["archive", "warehouse"], all.Select(d => d.Database));

            var warehouse = all[1];
            Assert.Equal(3, warehouse.Total);
            Assert.Equal(1, warehouse.Added);
            Assert.Equal(2, warehouse.Changed);
            Assert.Equal(0, warehouse.Deleted);
            Assert.Equal(latest, warehouse.LastChangeUtc);

            // Since a day counts only what happened after it, so a database can drop out.
            var since = Uri.EscapeDataString(latest.AddDays(-10).ToString("O"));
            var recent = await GetAsync(client, token, $"/api/v1/schema-changes/databases?repoId={repo}&since={since}");
            Assert.Equal([1, 3], recent.Select(d => d.Total));
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.SchemaChanges.Where(c => c.RepoId == repo).ExecuteDeleteAsync();
        }
    }

    private static async Task<IReadOnlyList<SchemaChangeDatabaseDto>> GetAsync(HttpClient client, string token, string relativeUri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(relativeUri, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<IReadOnlyList<SchemaChangeDatabaseDto>>();
        Assert.NotNull(value);
        return value;
    }
}
