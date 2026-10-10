using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The Power BI report structure endpoint end to end: a subscriber's pages, the visuals on each, and every
/// field's role. This is the consumption-side answer to "what questions does this report already ask, and in
/// what shape", distinct from the subscriber dossier's queries/objects. Every seeded row is removed in a
/// finally so repeated runs stay isolated in a shared catalog.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LineageSubscriberReportApiTests
{
    [SkippableFact]
    public async Task DescribesPagesVisualsAndFieldRoles_AndHandlesTheEdgeCases()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = Guid.NewGuid();
        var subscriberKey = $"subscriber|report_{suffix}";
        var barehandKey = $"subscriber|barehand_{suffix}";
        var now = DateTime.UtcNow;

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Repos.Add(new CatalogRepo
                {
                    Id = repoId, Name = "report_" + suffix, FirstSeenUtc = now, LastSyncUtc = now,
                });

                db.Subscribers.Add(new CatalogSubscriber
                {
                    RepoId = repoId,
                    Name = $"Sales Report {suffix}",
                    Type = "PowerBI",
                    ObjectKey = subscriberKey,
                    File = "subscribers.yaml",
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                });

                // A subscriber that has queries but never declared pbix:, so it must still resolve with an
                // empty page list rather than a 404 - only an unknown KEY is a 404.
                db.Subscribers.Add(new CatalogSubscriber
                {
                    RepoId = repoId,
                    Name = $"Hand-authored {suffix}",
                    Type = "Tableau",
                    ObjectKey = barehandKey,
                    File = "subscribers.yaml",
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                });

                // Two report files under one directory subscriber, each with its own "Page 1", proving the
                // report-file key keeps them apart rather than colliding.
                var page1A = $"{subscriberKey}#sales-a.pbix#1";
                var page1B = $"{subscriberKey}#sales-b.pbix#1";

                db.SubscriberReportPages.Add(new CatalogSubscriberReportPage
                {
                    RepoId = repoId,
                    SubscriberKey = subscriberKey,
                    PageKey = page1A,
                    ReportFile = "sales-a.pbix",
                    Ordinal = 1,
                    Name = "ReportSection1",
                    DisplayName = "Page 1",
                });
                db.SubscriberReportPages.Add(new CatalogSubscriberReportPage
                {
                    RepoId = repoId,
                    SubscriberKey = subscriberKey,
                    PageKey = page1B,
                    ReportFile = "sales-b.pbix",
                    Ordinal = 1,
                    Name = "ReportSection1",
                    DisplayName = "Page 1",
                });

                var visualKey = $"{page1A}#1";
                db.SubscriberReportVisuals.Add(new CatalogSubscriberReportVisual
                {
                    RepoId = repoId,
                    PageKey = page1A,
                    VisualKey = visualKey,
                    Ordinal = 1,
                    VisualType = "areaChart",
                    Title = "Sales Amount by Month",
                    QueryName = "sales-a.pbix / Page 1 / Sales Amount by Month",
                });

                db.SubscriberReportFields.Add(new CatalogSubscriberReportField
                {
                    RepoId = repoId,
                    VisualKey = visualKey,
                    Role = "Category",
                    TableName = "Date",
                    ColumnOrMeasure = "Month",
                    IsMeasure = false,
                });
                db.SubscriberReportFields.Add(new CatalogSubscriberReportField
                {
                    RepoId = repoId,
                    VisualKey = visualKey,
                    Role = "Y",
                    TableName = "Sales",
                    ColumnOrMeasure = "Sales Amount",
                    IsMeasure = true,
                });

                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueReadTokenAsync(client);

            var report = await GetJsonAsync<SubscriberReportDto>(
                client, token, $"/api/v1/lineage/subscribers/report?key={Uri.EscapeDataString(subscriberKey)}");

            Assert.Equal(subscriberKey, report.SubscriberKey);
            Assert.Equal(2, report.Pages.Count);

            // Both report files' "Page 1" survive as distinct entries, ordered by report file.
            var pageA = report.Pages.Single(p => p.ReportFile == "sales-a.pbix");
            var pageB = report.Pages.Single(p => p.ReportFile == "sales-b.pbix");
            Assert.Equal("Page 1", pageA.DisplayName);
            Assert.Equal("Page 1", pageB.DisplayName);
            Assert.Empty(pageB.Visuals);

            var visual = Assert.Single(pageA.Visuals);
            Assert.Equal("areaChart", visual.VisualType);
            Assert.Equal("Sales Amount by Month", visual.Title);
            // The visual's query name is the join key back to describe_subscriber's queries; it must round
            // trip exactly so a caller can match it without fuzzy text comparison.
            Assert.Equal("sales-a.pbix / Page 1 / Sales Amount by Month", visual.QueryName);

            Assert.Collection(
                visual.Fields,
                f =>
                {
                    Assert.Equal("Category", f.Role);
                    Assert.Equal("Date", f.TableName);
                    Assert.Equal("Month", f.ColumnOrMeasure);
                    Assert.False(f.IsMeasure);
                },
                f =>
                {
                    Assert.Equal("Y", f.Role);
                    Assert.Equal("Sales", f.TableName);
                    Assert.Equal("Sales Amount", f.ColumnOrMeasure);
                    Assert.True(f.IsMeasure);
                });

            // A subscriber with no extracted report resolves with an empty list, not a 404.
            var barehand = await GetJsonAsync<SubscriberReportDto>(
                client, token, $"/api/v1/lineage/subscribers/report?key={Uri.EscapeDataString(barehandKey)}");
            Assert.Empty(barehand.Pages);

            // An unknown key is a 404, not an empty answer that reads as "no report exists".
            using var missing = await SendAsync(
                client, token, "/api/v1/lineage/subscribers/report?key=nope|nope|nope|nope");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.SubscriberReportFields.Where(f => f.RepoId == repoId).ExecuteDeleteAsync();
            await db.SubscriberReportVisuals.Where(v => v.RepoId == repoId).ExecuteDeleteAsync();
            await db.SubscriberReportPages.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Subscribers.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
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
