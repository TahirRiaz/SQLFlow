using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core.Identity;
using SqlFlow.Lineage.Collection;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The Power BI reports the semantic layer holds, at the HTTP boundary. A report is only ever stored in its validated,
/// canonical form and only for a Power BI subscriber the repo declares; a raw report is never parsed by the control
/// plane, only forwarded to the isolated extractor, whose answer is validated again before anyone sees it.
/// </summary>
public sealed class SemanticReportApiTests
{
    private const string Route = "/api/v1/powerai/semantic-layer/reports";
    private const string ExtractorKey = "test-extractor-key-0123456789-abcdefghijklmnop";

    internal static string SampleSpec()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "samples", "powerbi", "AdventureWorks_Sales.spec.yaml");
        Assert.True(File.Exists(path), $"Expected the sample specification at {Path.GetFullPath(path)}.");
        return File.ReadAllText(path);
    }

    // ---- storing, listing, deleting (catalog-backed) -------------------------------------------------------

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task AStoredReport_IsCanonical_ListedWithItsSubscriber_AndReplacedByTheNextUpload()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var repo = await SeedRepoAsync(cs);

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read", "operate", "admin"]);
        try
        {
            var spec = SampleSpec().Replace(
                "Sql.Database(\\\"<redacted>\\\", \\\"AdventureWorks\\\")",
                "Sql.Database(\\\"Server=x;Password=hunter2;\\\", \\\"AdventureWorks\\\")",
                StringComparison.Ordinal);

            StoreSemanticReportSpecResult first;
            using (var response = await SendAsync(client, token, HttpMethod.Post, Route,
                new StoreSemanticReportSpecRequest(repo.Id, "Sales_Report", "Sales.pbix", spec)))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                first = (await response.Content.ReadFromJsonAsync<StoreSemanticReportSpecResult>())!;
            }

            Assert.False(first.Replaced);
            // A repo synced from a local path has no managed sync to queue.
            Assert.False(first.SyncQueued);
            Assert.Equal("Sales_Report", first.Report.Report.SubscriberName);
            Assert.True(first.Report.Report.SubscriberDeclared);
            Assert.Equal(ReportSpecOrigin.Upload, first.Report.Report.Origin);
            Assert.Equal((3, 5, 8, 1), (first.Report.Report.Pages, first.Report.Report.Visuals, first.Report.Report.Tables, first.Report.Report.Measures));
            Assert.Equal(7, first.Report.Summary.ResolvedTables);
            Assert.DoesNotContain("hunter2", first.Report.Spec, StringComparison.Ordinal);

            await using (var db = CatalogDatabase.Create(cs))
            {
                var row = await db.SemanticReportSpecs.AsNoTracking().SingleAsync(s => s.RepoId == repo.Id);
                Assert.DoesNotContain("hunter2", row.Spec, StringComparison.Ordinal);
                Assert.Equal(ReportSpecs.Hash(row.Spec), row.ContentHash);
                Assert.Equal(repo.SubscriberKey, row.SubscriberKey);
                Assert.False(string.IsNullOrEmpty(row.UpdatedBy));
            }

            using (var response = await SendAsync(client, token, HttpMethod.Post, Route,
                new StoreSemanticReportSpecRequest(repo.Id, "sales_report", "Sales.pbix", SampleSpec())))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var second = (await response.Content.ReadFromJsonAsync<StoreSemanticReportSpecResult>())!;
                Assert.True(second.Replaced);
                Assert.Equal(first.Report.Report.Id, second.Report.Report.Id);
            }

            using (var response = await SendAsync(client, token, HttpMethod.Get, $"{Route}?repoId={repo.Id}", body: null))
            {
                response.EnsureSuccessStatusCode();
                var list = (await response.Content.ReadFromJsonAsync<List<SemanticReportSpecDto>>())!;
                var listed = Assert.Single(list);
                Assert.Equal(repo.Name, listed.RepoName);
                Assert.Equal("Sales.pbix", listed.ReportFile);
            }

            // What a sync would have served: a page of this report with a questioned visual, and a page of another
            // report on the same subscriber, which the detail must not mix in.
            await using (var db = CatalogDatabase.Create(cs))
            {
                foreach (var (reportFile, question) in new[] { ("Sales.pbix", "Which region sells most?"), ("Other.pbix", "Unrelated?") })
                {
                    var pageKey = $"{repo.SubscriberKey}#{reportFile}#1";
                    db.SubscriberReportPages.Add(new CatalogSubscriberReportPage
                    {
                        RepoId = repo.Id, SubscriberKey = repo.SubscriberKey, PageKey = pageKey, ReportFile = reportFile,
                        Ordinal = 1, Name = "ReportSection1", DisplayName = "Overview",
                    });
                    db.SubscriberReportVisuals.Add(new CatalogSubscriberReportVisual
                    {
                        RepoId = repo.Id, PageKey = pageKey, VisualKey = $"{pageKey}#1", Ordinal = 1, VisualType = "barChart",
                        Title = "Sales by Region", QueryName = $"{reportFile} / Overview / Sales by Region",
                        ContentHash = SubscriberReportVisualHash.Compute("Sales by Region", "barChart", []),
                    });
                    db.SubscriberReportVisualQuestions.Add(new CatalogSubscriberReportVisualQuestion
                    {
                        RepoId = repo.Id, VisualKey = $"{pageKey}#1", Ordinal = 1, Question = question,
                    });
                }

                await db.SaveChangesAsync();
            }

            using (var response = await SendAsync(client, token, HttpMethod.Get, $"{Route}/{first.Report.Report.Id}", body: null))
            {
                response.EnsureSuccessStatusCode();
                var detail = (await response.Content.ReadFromJsonAsync<SemanticReportSpecDetailDto>())!;
                Assert.Equal(3, ReportSpecs.Inspect(detail.Spec, "the stored specification").Pages);
                var page = Assert.Single(detail.Pages);
                Assert.Equal("Sales.pbix", page.ReportFile);
                var visual = Assert.Single(page.Visuals);
                Assert.Equal(["Which region sells most?"], visual.Questions);
                var generated = Assert.Single(visual.QuestionEntries);
                Assert.Equal(SubscriberQuestionOrigin.Generated, generated.Origin);

                // A person adds a question to the visual...
                SubscriberReportQuestionDto added;
                using (var add = await SendAsync(client, token, HttpMethod.Post, $"{Route}/questions",
                    new AddReportQuestionRequest(repo.Id, visual.VisualKey, "  Which region grew fastest?  ")))
                {
                    Assert.Equal(HttpStatusCode.OK, add.StatusCode);
                    added = (await add.Content.ReadFromJsonAsync<SubscriberReportQuestionDto>())!;
                    Assert.Equal("Which region grew fastest?", added.Question);
                    Assert.Equal(SubscriberQuestionOrigin.Manual, added.Origin);
                    Assert.False(string.IsNullOrEmpty(added.UpdatedBy));
                }

                // ...but not the same one twice, not an empty or multi-line one, and not on a visual that is gone.
                using (var twice = await SendAsync(client, token, HttpMethod.Post, $"{Route}/questions",
                    new AddReportQuestionRequest(repo.Id, visual.VisualKey, "which region GREW fastest?")))
                {
                    Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
                }

                using (var blank = await SendAsync(client, token, HttpMethod.Post, $"{Route}/questions",
                    new AddReportQuestionRequest(repo.Id, visual.VisualKey, "   ")))
                {
                    Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
                }

                using (var multiline = await SendAsync(client, token, HttpMethod.Post, $"{Route}/questions",
                    new AddReportQuestionRequest(repo.Id, visual.VisualKey, "one\ntwo")))
                {
                    Assert.Equal(HttpStatusCode.BadRequest, multiline.StatusCode);
                }

                using (var missing = await SendAsync(client, token, HttpMethod.Post, $"{Route}/questions",
                    new AddReportQuestionRequest(repo.Id, $"{visual.VisualKey}-gone", "Anything?")))
                {
                    Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
                }

                // Editing a generated question makes it the person's.
                using (var edit = await SendAsync(client, token, HttpMethod.Put, $"{Route}/questions/{generated.Id}",
                    new UpdateReportQuestionRequest("Which region sells the most units?")))
                {
                    Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
                    var edited = (await edit.Content.ReadFromJsonAsync<SubscriberReportQuestionDto>())!;
                    Assert.Equal("Which region sells the most units?", edited.Question);
                    Assert.Equal(SubscriberQuestionOrigin.Manual, edited.Origin);
                }

                using (var clash = await SendAsync(client, token, HttpMethod.Put, $"{Route}/questions/{generated.Id}",
                    new UpdateReportQuestionRequest("Which region grew fastest?")))
                {
                    Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
                }

                using (var delete = await SendAsync(client, token, HttpMethod.Delete, $"{Route}/questions/{added.Id}", body: null))
                {
                    Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
                }

                using (var again = await SendAsync(client, token, HttpMethod.Delete, $"{Route}/questions/{added.Id}", body: null))
                {
                    Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
                }
            }

            using (var response = await SendAsync(client, token, HttpMethod.Get, $"{Route}/{first.Report.Report.Id}", body: null))
            {
                response.EnsureSuccessStatusCode();
                var detail = (await response.Content.ReadFromJsonAsync<SemanticReportSpecDetailDto>())!;
                var entry = Assert.Single(Assert.Single(Assert.Single(detail.Pages).Visuals).QuestionEntries);
                Assert.Equal(("Which region sells the most units?", SubscriberQuestionOrigin.Manual), (entry.Question, entry.Origin));
            }

            using (var response = await SendAsync(client, token, HttpMethod.Get, $"{Route}/subscribers", body: null))
            {
                response.EnsureSuccessStatusCode();
                var subscribers = (await response.Content.ReadFromJsonAsync<List<SemanticReportSubscriberDto>>())!;
                Assert.Contains(subscribers, s => s.RepoId == repo.Id && s.Name == "Sales_Report");
                // Only Power BI subscribers can hold a report.
                Assert.DoesNotContain(subscribers, s => s.RepoId == repo.Id && s.Name == "Revenue_Workbook");
            }

            using (var response = await SendAsync(client, token, HttpMethod.Delete, $"{Route}/{first.Report.Report.Id}", body: null))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            using (var response = await SendAsync(client, token, HttpMethod.Delete, $"{Route}/{first.Report.Report.Id}", body: null))
            {
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }
        }
        finally
        {
            await DeleteRepoAsync(cs, repo.Id);
        }
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task StoringAReport_QueuesTheManagedSyncOfAGitRepo()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var repo = await SeedRepoAsync(cs);

        await using (var db = CatalogDatabase.Create(cs))
        {
            // A placeholder remote nothing ever clones: the other hosts sharing this test catalog see its next sync a
            // year out, and this test's own host runs with the managed sync switched off.
            db.RepoSources.Add(new CatalogRepoSource
            {
                Id = Guid.NewGuid(),
                Name = repo.Name,
                RemoteUrl = "https://example.invalid/estate.git",
                Branch = "main",
                Enabled = true,
                SyncIntervalSeconds = 3600,
                NextSyncUtc = DateTime.UtcNow.AddYears(1),
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await using var factory = new ControlPlaneAppFactory()
            .WithCatalog(cs)
            .WithSetting("ControlPlane:ManagedSync:Enabled", "false");
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read", "operate", "admin"]);
        try
        {
            using var response = await SendAsync(client, token, HttpMethod.Post, Route,
                new StoreSemanticReportSpecRequest(repo.Id, "Sales_Report", "Sales.pbix", SampleSpec()));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True((await response.Content.ReadFromJsonAsync<StoreSemanticReportSpecResult>())!.SyncQueued);

            await using var db = CatalogDatabase.Create(cs);
            var source = await db.RepoSources.AsNoTracking().SingleAsync(s => s.Name == repo.Name);
            Assert.True(source.ForceLineageOnNextSync);
            Assert.True(source.NextSyncUtc <= DateTime.UtcNow.AddMinutes(1));
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.RepoSources.Where(s => s.Name == repo.Name).ExecuteDeleteAsync();
            await DeleteRepoAsync(cs, repo.Id);
        }
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task AReport_IsRefused_UnlessItIsAValidSpecificationForADeclaredPowerBiSubscriber()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var repo = await SeedRepoAsync(cs);

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read", "operate", "admin"]);
        try
        {
            async Task ExpectAsync(HttpStatusCode status, StoreSemanticReportSpecRequest request, string detailFragment)
            {
                using var response = await SendAsync(client, token, HttpMethod.Post, Route, request);
                Assert.Equal(status, response.StatusCode);
                Assert.Contains(detailFragment, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            await ExpectAsync(HttpStatusCode.BadRequest,
                new StoreSemanticReportSpecRequest(repo.Id, "Sales_Report", "Sales.pbix", "subscribers: {}"),
                "no single report specification");
            await ExpectAsync(HttpStatusCode.BadRequest,
                new StoreSemanticReportSpecRequest(repo.Id, "Sales_Report", "../Sales.pbix", SampleSpec()),
                "relative path");
            await ExpectAsync(HttpStatusCode.NotFound,
                new StoreSemanticReportSpecRequest(repo.Id, "No_Such_Report", "Sales.pbix", SampleSpec()),
                "declares no subscriber");
            await ExpectAsync(HttpStatusCode.BadRequest,
                new StoreSemanticReportSpecRequest(repo.Id, "Revenue_Workbook", "Sales.pbix", SampleSpec()),
                "type PowerBI");
            await ExpectAsync(HttpStatusCode.NotFound,
                new StoreSemanticReportSpecRequest(Guid.NewGuid(), "Sales_Report", "Sales.pbix", SampleSpec()),
                "No repo");

            await using var db = CatalogDatabase.Create(cs);
            Assert.False(await db.SemanticReportSpecs.AnyAsync(s => s.RepoId == repo.Id));
        }
        finally
        {
            await DeleteRepoAsync(cs, repo.Id);
        }
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task CuratingReports_RequiresTheAdminScope()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read", "operate"]);

        using var list = await SendAsync(client, token, HttpMethod.Get, Route, body: null);
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);

        using var extract = await SendRawAsync(client, token, $"{Route}/extract?reportFile=Sales.pbix", [1, 2, 3]);
        Assert.Equal(HttpStatusCode.Forbidden, extract.StatusCode);
    }

    // ---- question generation switch ------------------------------------------------------------------------

    [SkippableFact]
    public async Task QuestionGeneration_CannotBeTurnedOn_WithoutAnAnthropicKey()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read", "operate", "admin"]);
        var before = await ReadQuestionGenerationColumnsAsync(cs);
        try
        {
            using (var response = await SendAsync(client, token, HttpMethod.Get, $"{Route}/question-generation", body: null))
            {
                response.EnsureSuccessStatusCode();
                var state = (await response.Content.ReadFromJsonAsync<QuestionGenerationDto>())!;
                Assert.False(state.Available);
                Assert.False(state.Enabled);
            }

            using (var response = await SendAsync(client, token, HttpMethod.Put, $"{Route}/question-generation",
                new SetQuestionGenerationRequest(true)))
            {
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                Assert.Contains("Anthropic key", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            // Turning it off needs no key.
            using (var response = await SendAsync(client, token, HttpMethod.Put, $"{Route}/question-generation",
                new SetQuestionGenerationRequest(false)))
            {
                response.EnsureSuccessStatusCode();
                var state = (await response.Content.ReadFromJsonAsync<QuestionGenerationDto>())!;
                Assert.False(state.Override);
                Assert.False(state.Enabled);
                Assert.False(string.IsNullOrEmpty(state.UpdatedBy));
            }
        }
        finally
        {
            await RestoreQuestionGenerationColumnsAsync(cs, before);
        }
    }

    [SkippableFact]
    public async Task QuestionGeneration_AdminOverridesTheDeploymentDefault_UntilReset()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs)
            .WithSetting("ControlPlane:Assistant:Anthropic:ApiKey", "sk-ant-test-key-never-called");
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read", "operate", "admin"]);
        var before = await ReadQuestionGenerationColumnsAsync(cs);
        try
        {
            // The deployment default is off (the shipped setting), so an admin turning it on is what enables it.
            using (var response = await SendAsync(client, token, HttpMethod.Put, $"{Route}/question-generation",
                new SetQuestionGenerationRequest(null)))
            {
                response.EnsureSuccessStatusCode();
                var state = (await response.Content.ReadFromJsonAsync<QuestionGenerationDto>())!;
                Assert.True(state.Available);
                Assert.False(state.DeploymentDefault);
                Assert.Null(state.Override);
                Assert.False(state.Enabled);
            }

            using (var response = await SendAsync(client, token, HttpMethod.Put, $"{Route}/question-generation",
                new SetQuestionGenerationRequest(true)))
            {
                response.EnsureSuccessStatusCode();
                var state = (await response.Content.ReadFromJsonAsync<QuestionGenerationDto>())!;
                Assert.True(state.Override);
                Assert.True(state.Enabled);
            }

            using (var capabilities = await SendAsync(client, token, HttpMethod.Get, $"{Route}/capabilities", body: null))
            {
                capabilities.EnsureSuccessStatusCode();
                var body = (await capabilities.Content.ReadFromJsonAsync<SemanticReportCapabilitiesDto>())!;
                Assert.True(body.QuestionGenerationEnabled);
            }

            // Every sync path asks the registered switch, so it must now say yes.
            await using (var db = CatalogDatabase.Create(cs))
            {
                var questionGeneration = factory.Services.GetRequiredService<QuestionGenerationSwitch>();
                Assert.True(await questionGeneration.IsEnabledAsync(db, CancellationToken.None));
            }

            using (var response = await SendAsync(client, token, HttpMethod.Put, $"{Route}/question-generation",
                new SetQuestionGenerationRequest(null)))
            {
                response.EnsureSuccessStatusCode();
                var state = (await response.Content.ReadFromJsonAsync<QuestionGenerationDto>())!;
                Assert.Null(state.Override);
                Assert.False(state.Enabled);
                Assert.Equal(0, state.SyncsQueued);
            }
        }
        finally
        {
            await RestoreQuestionGenerationColumnsAsync(cs, before);
        }
    }

    /// <summary>The switch lives on the catalog's one settings row, which other tests share, so each test puts back
    /// exactly what it found.</summary>
    private static async Task<QuestionGenerationColumns> ReadQuestionGenerationColumnsAsync(string cs)
    {
        await using var db = CatalogDatabase.Create(cs);
        var row = await db.SemanticLayerSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == CatalogSemanticLayerSettings.SingletonId);
        return new QuestionGenerationColumns(
            row is not null, row?.QuestionGeneration, row?.QuestionGenerationUpdatedBy, row?.QuestionGenerationUpdatedUtc);
    }

    private static async Task RestoreQuestionGenerationColumnsAsync(string cs, QuestionGenerationColumns before)
    {
        await using var db = CatalogDatabase.Create(cs);
        var rows = db.SemanticLayerSettings.Where(s => s.Id == CatalogSemanticLayerSettings.SingletonId);
        if (!before.Exists)
        {
            // The test created the row; remove it unless something wrote instructions to it meanwhile.
            await rows.Where(s => s.Instructions == null).ExecuteDeleteAsync();
            return;
        }

        await rows.ExecuteUpdateAsync(u => u
            .SetProperty(s => s.QuestionGeneration, before.Value)
            .SetProperty(s => s.QuestionGenerationUpdatedBy, before.UpdatedBy)
            .SetProperty(s => s.QuestionGenerationUpdatedUtc, before.UpdatedUtc));
    }

    private sealed record QuestionGenerationColumns(bool Exists, bool? Value, string? UpdatedBy, DateTime? UpdatedUtc);

    // ---- extraction (no catalog needed) --------------------------------------------------------------------

    [Fact]
    public async Task Extraction_IsRefusedPlainly_WhenItIsNotEnabled()
    {
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read", "operate", "admin"]);

        using (var capabilities = await SendAsync(client, token, HttpMethod.Get, $"{Route}/capabilities", body: null))
        {
            capabilities.EnsureSuccessStatusCode();
            var body = (await capabilities.Content.ReadFromJsonAsync<SemanticReportCapabilitiesDto>())!;
            Assert.False(body.ExtractionEnabled);
            Assert.Equal(ReportSpecs.MaxBytes, body.MaxSpecBytes);
            Assert.False(body.QuestionGenerationEnabled);
        }

        using var response = await SendRawAsync(client, token, $"{Route}/extract?reportFile=Sales.pbix", [1, 2, 3]);
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Contains("sqlflow powerbi extract", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extraction_ForwardsTheReportToTheExtractor_AndAnswersWithTheValidatedSpecification()
    {
        var extractor = new StubExtractor(_ => (HttpStatusCode.OK, SampleSpec(), "application/yaml"));
        await using var factory = ExtractionEnabled(extractor, maxUploadMegabytes: 4);
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read", "operate", "admin"]);
        var report = Encoding.UTF8.GetBytes("PK fake report bytes");

        using var response = await SendRawAsync(client, token, $"{Route}/extract?reportFile=team%2FSales%20Q3.pbix", report);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<ExtractedSemanticReportDto>())!;
        Assert.Equal("team/Sales Q3.pbix", body.ReportFile);
        Assert.Equal(3, body.Summary.Pages);
        Assert.Equal(ReportSpecs.Normalize(SampleSpec(), "x"), body.Spec);

        var call = Assert.Single(extractor.Calls);
        Assert.Equal(ReportExtractionProtocol.ExtractPath, call.Path);
        Assert.Equal("team/Sales Q3.pbix", call.ReportFile);
        Assert.Equal(ExtractorKey, call.Key);
        Assert.Equal(report, call.Body);
    }

    [Fact]
    public async Task Extraction_PassesOnTheExtractorsRefusal()
    {
        var extractor = new StubExtractor(_ => (
            HttpStatusCode.UnprocessableEntity,
            """{"title":"The report could not be extracted","status":422,"detail":"the file has no readable model or report layer"}""",
            "application/problem+json"));
        await using var factory = ExtractionEnabled(extractor, maxUploadMegabytes: 4);
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read", "operate", "admin"]);

        using var response = await SendRawAsync(client, token, $"{Route}/extract?reportFile=Sales.pbix", [1, 2, 3]);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("no readable model", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extraction_DoesNotTrustTheExtractorsAnswer()
    {
        var extractor = new StubExtractor(_ => (HttpStatusCode.OK, "subscribers:\n  r:\n    nodes: &a []\n    edges: *a\n", "application/yaml"));
        await using var factory = ExtractionEnabled(extractor, maxUploadMegabytes: 4);
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read", "operate", "admin"]);

        using var response = await SendRawAsync(client, token, $"{Route}/extract?reportFile=Sales.pbix", [1, 2, 3]);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task Extraction_RefusesAnOversizedReport_WithoutSendingIt()
    {
        var extractor = new StubExtractor(_ => (HttpStatusCode.OK, SampleSpec(), "application/yaml"));
        await using var factory = ExtractionEnabled(extractor, maxUploadMegabytes: 1);
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read", "operate", "admin"]);

        using var response = await SendRawAsync(
            client, token, $"{Route}/extract?reportFile=Sales.pbix", new byte[(1024 * 1024) + 1]);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(extractor.Calls);
    }

    [Fact]
    public async Task Extraction_RefusesALabelThatCannotKeyARow()
    {
        var extractor = new StubExtractor(_ => (HttpStatusCode.OK, SampleSpec(), "application/yaml"));
        await using var factory = ExtractionEnabled(extractor, maxUploadMegabytes: 1);
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read", "operate", "admin"]);

        using var response = await SendRawAsync(client, token, $"{Route}/extract?reportFile=Sales%232.pbix", [1]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(extractor.Calls);
    }

    [Fact]
    public async Task Extraction_ReportsAnUnreachableExtractor()
    {
        var extractor = new StubExtractor(_ => throw new HttpRequestException("connection refused"));
        await using var factory = ExtractionEnabled(extractor, maxUploadMegabytes: 1);
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read", "operate", "admin"]);

        using var response = await SendRawAsync(client, token, $"{Route}/extract?reportFile=Sales.pbix", [1]);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("could not be reached", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public void EnablingExtraction_WithoutAnEndpointOrKey_FailsAtStartup()
    {
        var options = new Configuration.ReportExtractionOptions { Enabled = true };

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("Endpoint", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ApiKey", ex.Message, StringComparison.Ordinal);
    }

    // ---- plumbing ------------------------------------------------------------------------------------------

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> ExtractionEnabled(
        StubExtractor extractor, int maxUploadMegabytes)
        => new ControlPlaneAppFactory()
            .WithSetting("ControlPlane:PowerAI:ReportExtraction:Enabled", "true")
            .WithSetting("ControlPlane:PowerAI:ReportExtraction:Endpoint", "http://pbix-extractor.test:8080")
            .WithSetting("ControlPlane:PowerAI:ReportExtraction:ApiKey", ExtractorKey)
            .WithSetting("ControlPlane:PowerAI:ReportExtraction:MaxUploadMegabytes", maxUploadMegabytes.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.AddHttpClient(ReportExtractionClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => extractor)));

    private sealed record ExtractorCall(string Path, string? ReportFile, string? Key, byte[] Body);

    /// <summary>Stands in for the isolated extractor service: records what it was sent and answers as told.</summary>
    private sealed class StubExtractor(Func<ExtractorCall, (HttpStatusCode Status, string Body, string MediaType)> answer)
        : HttpMessageHandler
    {
        public List<ExtractorCall> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var call = new ExtractorCall(
                request.RequestUri.AbsolutePath,
                query["reportFile"],
                request.Headers.TryGetValues(ReportExtractionProtocol.KeyHeader, out var keys) ? keys.Single() : null,
                body);
            Calls.Add(call);
            var (status, text, mediaType) = answer(call);
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, mediaType) };
        }
    }

    private sealed record SeededRepo(Guid Id, string Name, string SubscriberKey);

    private static async Task<SeededRepo> SeedRepoAsync(string cs)
    {
        var name = "reports_" + Guid.NewGuid().ToString("N")[..8];
        var repoId = FlowIdentity.FromName(name);
        var now = DateTime.UtcNow;
        var salesKey = NodeKey.For(ServerIdentity.Subscriber, null, null, "Sales_Report");

        await using var db = CatalogDatabase.Create(cs);
        db.Repos.Add(new CatalogRepo { Id = repoId, Name = name, RootPath = "/nowhere", FirstSeenUtc = now, LastSyncUtc = now });
        db.Subscribers.Add(new CatalogSubscriber
        {
            RepoId = repoId, Name = "Sales_Report", Type = "Power BI", ObjectKey = salesKey,
            File = "subscribers.yaml", FirstSeenUtc = now, LastSeenUtc = now,
        });
        db.Subscribers.Add(new CatalogSubscriber
        {
            RepoId = repoId, Name = "Revenue_Workbook", Type = "Excel",
            ObjectKey = NodeKey.For(ServerIdentity.Subscriber, null, null, "Revenue_Workbook"),
            File = "subscribers.yaml", FirstSeenUtc = now, LastSeenUtc = now,
        });
        await db.SaveChangesAsync();
        return new SeededRepo(repoId, name, salesKey);
    }

    private static async Task DeleteRepoAsync(string cs, Guid repoId)
    {
        await using var db = CatalogDatabase.Create(cs);
        await RepoStore.DeleteAsync(db, repoId);
    }

    private static async Task<string> IssueTokenAsync(HttpClient client, string[] scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, string token, HttpMethod method, string path, object? body)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendRawAsync(HttpClient client, string token, string path, byte[] body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
}
