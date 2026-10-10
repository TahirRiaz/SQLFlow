using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Identity;
using SqlFlow.Lineage.Collection;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// The catalog side of report specifications. A sync reads the specifications the semantic layer holds, so a report
/// the syncing machine cannot extract (the control plane never can) keeps its pages and model instead of being wiped
/// by every pass; and a change to what the consumption side reads (an upload, an edited subscribers.yaml) is applied
/// by an ordinary sync even though no flow changed.
/// </summary>
[Trait("Category", "Integration")]
[Collection(PbixExtractorEnvironment.Name)]
public sealed class CatalogSyncReportSpecTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_specsync_" + Guid.NewGuid().ToString("N"));

    public CatalogSyncReportSpecTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly string SubscriberKey = NodeKey.For(ServerIdentity.Subscriber, null, null, "Sales_Report");

    private void WriteEstate(string? pbix, bool declareSubscriber = true)
    {
        File.WriteAllText(Path.Combine(_dir, "10_sales_ing.yaml"), """
            flowType: ing
            name: sales_10_ing
            connections:
              pre: ${env:SQLFLOW_CONN_PRE}
              dwh: ${env:SQLFLOW_CONN_DWH}
            source:
              server: pre
              object: "[PreDb].[pre].[v_Sales]"
            target:
              server: dwh
              object: "[AdventureWorks].[dbo].[FactResellerSales]"
            load:
              keyColumns: [id]
            """);

        var lines = new List<string>
        {
            "connections:",
            "  dwh: ${env:SQLFLOW_CONN_DWH}",
            "subscribers:",
        };
        lines.Add(declareSubscriber ? "  Sales_Report:" : "  Other_Report:");
        lines.Add("    type: PowerBI");
        lines.Add("    server: dwh");
        if (pbix is not null)
        {
            lines.Add($"    pbix: {pbix}");
        }

        File.WriteAllText(Path.Combine(_dir, "subscribers.yaml"), string.Join('\n', lines) + '\n');
    }

    private static CatalogSemanticReportSpec Spec(Guid repoId, string origin, string yaml)
    {
        var canonical = ReportSpecs.Normalize(yaml, "the test specification");
        var summary = ReportSpecs.Inspect(canonical, "the test specification");
        return new CatalogSemanticReportSpec
        {
            RepoId = repoId,
            SubscriberKey = SubscriberKey,
            ReportFile = "Sales.pbix",
            Origin = origin,
            Spec = canonical,
            ContentHash = ReportSpecs.Hash(canonical),
            IdentityHash = SemanticReportSpecIdentity.Compute(repoId, SubscriberKey, origin, "Sales.pbix"),
            Pages = summary.Pages,
            Visuals = summary.Visuals,
            Tables = summary.Tables,
            Measures = summary.Measures,
            UpdatedUtc = DateTime.UtcNow,
        };
    }

    private static async Task<(string Cs, string Repo, Guid RepoId)> PrepareAsync()
    {
        var cs = IntegrationDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var repo = "specsync_" + Guid.NewGuid().ToString("N")[..8];
        return (cs, repo, FlowIdentity.FromName(repo));
    }

    private async Task SyncAsync(string cs, string repo)
    {
        await using var db = CatalogDatabase.Create(cs);
        await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
    }

    private static async Task<(int Pages, int ModelTables, int Queries)> CountAsync(string cs, Guid repoId)
    {
        await using var db = CatalogDatabase.Create(cs);
        return (
            await db.SubscriberReportPages.CountAsync(p => p.RepoId == repoId && p.SubscriberKey == SubscriberKey),
            await db.SubscriberModelTables.CountAsync(t => t.RepoId == repoId && t.SubscriberKey == SubscriberKey),
            await db.SubscriberQueries.CountAsync(q => q.RepoId == repoId && q.SubscriberKey == SubscriberKey));
    }

    private static async Task CleanupAsync(string cs, string repo, Guid repoId)
    {
        await using var db = CatalogDatabase.Create(cs);
        await RepoStore.DeleteAsync(db, repoId);
        Assert.False(await db.SemanticReportSpecs.AnyAsync(s => s.RepoId == repoId));
        Assert.False(await db.SubscriberReportPages.AnyAsync(p => p.RepoId == repoId));
        Assert.False(await db.Repos.AnyAsync(r => r.Name == repo));
    }

    [SkippableFact]
    public async Task AnUpload_IsAppliedByTheNextOrdinarySync_AndRemovedByTheOneAfterItsDeletion()
    {
        var (cs, repo, repoId) = await PrepareAsync();
        WriteEstate(pbix: null);
        try
        {
            await SyncAsync(cs, repo);
            Assert.Equal((0, 0, 0), await CountAsync(cs, repoId));

            await using (var db = CatalogDatabase.Create(cs))
            {
                db.SemanticReportSpecs.Add(Spec(repoId, ReportSpecOrigin.Upload, LineageReportSpecTests.SampleSpec()));
                await db.SaveChangesAsync();
            }

            // Nothing in the repository changed; the fingerprint alone makes this pass recompute the graph.
            await SyncAsync(cs, repo);
            Assert.Equal((3, 8, 5), await CountAsync(cs, repoId));

            await using (var db = CatalogDatabase.Create(cs))
            {
                var customer = await db.SubscriberModelTables.AsNoTracking()
                    .SingleAsync(t => t.RepoId == repoId && t.Name == "Customer");
                Assert.Equal(NodeKey.For("${env:SQLFLOW_CONN_DWH}", "AdventureWorks", "dbo", "DimCustomer"), customer.ObjectKey);
                Assert.NotNull((await db.Repos.AsNoTracking().SingleAsync(r => r.Id == repoId)).SubscriberInputHash);

                await db.SemanticReportSpecs.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
            }

            await SyncAsync(cs, repo);
            Assert.Equal((0, 0, 0), await CountAsync(cs, repoId));
        }
        finally
        {
            await CleanupAsync(cs, repo, repoId);
        }
    }

    [SkippableFact]
    public async Task AKeptExtraction_SurvivesASyncThatCannotSeeTheReport()
    {
        var (cs, repo, repoId) = await PrepareAsync();

        // The control plane's view: subscribers.yaml declares the report, the .pbix itself is not in the clone.
        WriteEstate(pbix: "reports/Sales.pbix");
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.SemanticReportSpecs.Add(Spec(repoId, ReportSpecOrigin.Extracted, LineageReportSpecTests.SampleSpec()));
                await db.SaveChangesAsync();
            }

            await SyncAsync(cs, repo);
            await SyncAsync(cs, repo);

            Assert.Equal((3, 8, 5), await CountAsync(cs, repoId));
            await using (var db = CatalogDatabase.Create(cs))
            {
                // Not authoritative about the report, so the kept copy is left exactly as it was.
                Assert.Single(await db.SemanticReportSpecs.AsNoTracking()
                    .Where(s => s.RepoId == repoId && s.Origin == ReportSpecOrigin.Extracted).ToListAsync());
            }
        }
        finally
        {
            await CleanupAsync(cs, repo, repoId);
        }
    }

    [SkippableFact]
    public async Task AKeptExtraction_IsDropped_OnceItsSubscriberIsNoLongerDeclared()
    {
        var (cs, repo, repoId) = await PrepareAsync();
        WriteEstate(pbix: "reports/Sales.pbix");
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.SemanticReportSpecs.Add(Spec(repoId, ReportSpecOrigin.Extracted, LineageReportSpecTests.SampleSpec()));
                db.SemanticReportSpecs.Add(Spec(repoId, ReportSpecOrigin.Upload, LineageReportSpecTests.SampleSpec()));
                await db.SaveChangesAsync();
            }

            await SyncAsync(cs, repo);

            WriteEstate(pbix: null, declareSubscriber: false);
            await SyncAsync(cs, repo);

            await using (var check = CatalogDatabase.Create(cs))
            {
                var remaining = await check.SemanticReportSpecs.AsNoTracking()
                    .Where(s => s.RepoId == repoId).Select(s => s.Origin).ToListAsync();

                // The sync's own copy goes with the declaration; a person's upload stays until a person removes it.
                Assert.Equal([ReportSpecOrigin.Upload], remaining);
            }

            Assert.Equal((0, 0, 0), await CountAsync(cs, repoId));
        }
        finally
        {
            await CleanupAsync(cs, repo, repoId);
        }
    }

    [SkippableFact]
    public async Task AKeptExtraction_IsDropped_OnceTheReportIsCommittedAsASpecification()
    {
        var (cs, repo, repoId) = await PrepareAsync();
        Directory.CreateDirectory(Path.Combine(_dir, "reports"));
        File.WriteAllText(Path.Combine(_dir, "reports", "Sales.pbix.yaml"), LineageReportSpecTests.SampleSpec());
        WriteEstate(pbix: "reports/Sales.pbix.yaml");
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.SemanticReportSpecs.Add(Spec(repoId, ReportSpecOrigin.Extracted, LineageReportSpecTests.SampleSpec()));
                await db.SaveChangesAsync();
            }

            await SyncAsync(cs, repo);

            Assert.Equal((3, 8, 5), await CountAsync(cs, repoId));
            await using var check = CatalogDatabase.Create(cs);
            Assert.False(await check.SemanticReportSpecs.AnyAsync(s => s.RepoId == repoId));
        }
        finally
        {
            await CleanupAsync(cs, repo, repoId);
        }
    }

    [SkippableFact]
    public async Task AnEditedSubscriberLibrary_IsAppliedWithoutAnyFlowChange()
    {
        var (cs, repo, repoId) = await PrepareAsync();
        WriteEstate(pbix: null, declareSubscriber: false);
        try
        {
            await SyncAsync(cs, repo);
            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.Equal(["Other_Report"], await db.Subscribers.AsNoTracking()
                    .Where(s => s.RepoId == repoId).Select(s => s.Name).ToListAsync());
            }

            WriteEstate(pbix: null);
            await SyncAsync(cs, repo);

            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.Equal(["Sales_Report"], await db.Subscribers.AsNoTracking()
                    .Where(s => s.RepoId == repoId).Select(s => s.Name).ToListAsync());
            }
        }
        finally
        {
            await CleanupAsync(cs, repo, repoId);
        }
    }

    [SkippableFact]
    public async Task AFreshExtraction_IsKept_ForTheNextSyncThatCannotExtract()
    {
        Skip.If(ReportSpecs.LocateExtractor() is null,
            $"The 'pbix-extract' tool was not found. Build it with 'make -C tools/pbix-extract' and set {ReportSpecs.ExtractorPathVariable}.");
        var (cs, repo, repoId) = await PrepareAsync();
        PbixFixture.Write(Path.Combine(_dir, "reports", "Sales.pbix"));
        WriteEstate(pbix: "reports/Sales.pbix");
        try
        {
            await SyncAsync(cs, repo);
            var extracted = await CountAsync(cs, repoId);
            Assert.True(extracted.Pages > 0);

            await using (var db = CatalogDatabase.Create(cs))
            {
                var kept = await db.SemanticReportSpecs.AsNoTracking()
                    .SingleAsync(s => s.RepoId == repoId && s.Origin == ReportSpecOrigin.Extracted);
                Assert.Equal("Sales.pbix", kept.ReportFile);
                Assert.Null(kept.UpdatedBy);
            }

            // The report disappears from this machine (a fresh clone of a repo that git-ignores it).
            File.Delete(Path.Combine(_dir, "reports", "Sales.pbix"));
            await using (var db = CatalogDatabase.Create(cs))
            {
                await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow, forceLineage: true);
            }

            Assert.Equal(extracted, await CountAsync(cs, repoId));
        }
        finally
        {
            await CleanupAsync(cs, repo, repoId);
        }
    }
}
