using SqlFlow.Core.Lineage;
using SqlFlow.Lineage;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// The derived tier against the real sink, end to end through the service: a five-flow estate over a live
/// view chain, procedure, synonym, and an encrypted module. Connecting must snap the plan into the correct
/// waves (the proc body writes the mart through the view, so the sp flow inherits both loaders as
/// dependencies and the export waits for the proc), resolve the synonym to its base, merge equal connection
/// references into one server identity, and surface the encrypted module as a node warning: everything the
/// hand-driven probe proved, pinned as a repeatable test.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LineageIntegrationTests : IDisposable
{
    private readonly LineageEstateHarness _estate = new();

    public void Dispose()
    {
        _estate.Dispose();
    }

    [SkippableFact]
    public async Task ConnectedTier_ExpandsModules_ResolvesSynonyms_MergesServerIdentities()
    {
        var cs = IntegrationDb.Require();
        var dbName = await IntegrationDb.ScalarAsync<string?>(cs, "SELECT DB_NAME();");
        await CreateEstateObjects(cs);

        // Two DIFFERENT canonical references to the same physical server: the identity-proof pass must
        // merge them. The legacy name is bridged by IntegrationDb, so both resolve.
        const string refA = "${env:SQLFLOW_TEST_DB}";
        const string refB = "${env:SQLFlowSinkConStr}";
        Environment.SetEnvironmentVariable("SQLFLOW_TEST_DB", cs);
        try
        {
            _estate
                .Flow("load-customers.flow.yaml", LineageEstateHarness.Ingestion(
                    "load-customers", $"{dbName}.dbo._SfLinT_CustomersRaw", $"{dbName}.dbo._SfLinT_Customers",
                    sourceRef: refA, targetRef: refA))
                .Flow("load-orders.flow.yaml", LineageEstateHarness.Ingestion(
                    "load-orders", $"{dbName}.dbo._SfLinT_OrdersRaw", $"{dbName}.dbo._SfLinT_Orders",
                    sourceRef: refB, targetRef: refB))
                .Flow("build-marts.flow.yaml", LineageEstateHarness.StoredProcedure(
                    "build-marts", $"{dbName}.dbo.usp_SfLinT_BuildMart", serverRef: refA))
                .Flow("orders-watch.flow.yaml", LineageEstateHarness.HealthCheck(
                    "orders-watch", $"{dbName}.dbo.syn_SfLinT_Orders", "OrderDate", serverRef: refB))
                .Flow("mart-export.flow.yaml", LineageEstateHarness.Export(
                    "mart-export", $"{dbName}.dbo._SfLinT_OrderMart", serverRef: refA));

            var report = await LineageService.ComputeAsync(new LineageOptions
            {
                FlowDirectory = _estate.Root,
                IncludeDerived = true,
            });

            // The plan: loaders first; the sp flow (proc body inherited: reads the view over BOTH loads,
            // writes the mart) and the synonym-resolved watcher next; the mart export last.
            Assert.Equal(
                [["load-customers", "load-orders"], ["build-marts", "orders-watch"], ["mart-export"]],
                LineageEstateHarness.Waves(report));
            Assert.Empty(report.Cycles);

            // The two references merged into ONE server identity: a single node space.
            Assert.Single(report.Objects.Select(o => o.ServerRef).Where(s => s.StartsWith("${env:", StringComparison.Ordinal)).Distinct());

            // The synonym dissolved: the watcher's dependency runs through the base table.
            var watcherDependency = Assert.Single(report.FlowDependencies, d => d.ToFlow == "orders-watch");
            Assert.Equal("load-orders", watcherDependency.FromFlow);
            Assert.Contains(watcherDependency.ViaObjects, v => v.EndsWith("_sflint_orders", StringComparison.Ordinal));

            // The encrypted module is a named, warned node: a declared gap, never a silent one.
            var encrypted = Assert.Single(report.Objects, o => o.Name.Equals("usp_SfLinT_Secret", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(encrypted.Warnings, w => w.Contains("encrypted", StringComparison.OrdinalIgnoreCase));

            // The proc's derived facts carry module attribution.
            Assert.Contains(report.Edges, e => e.Tier == LineageTier.Derived && e.ViaModule is not null
                && e.ObjectKey.EndsWith("_sflint_ordermart", StringComparison.Ordinal) && e.Relation == LineageRelation.Writes);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SQLFLOW_TEST_DB", null);
            await DropEstateObjects(cs);
        }
    }

    private static async Task CreateEstateObjects(string cs)
    {
        await DropEstateObjects(cs);
        await IntegrationDb.ExecuteAsync(cs, """
            CREATE TABLE dbo._SfLinT_Customers (Id int PRIMARY KEY, Name nvarchar(100));
            CREATE TABLE dbo._SfLinT_Orders (Id int PRIMARY KEY, CustomerId int, Amount money, OrderDate date);
            CREATE TABLE dbo._SfLinT_OrderMart (CustomerId int, Total money);
            """);
        await IntegrationDb.ExecuteAsync(cs,
            "CREATE VIEW dbo.vw_SfLinT_Summary AS SELECT o.CustomerId, c.Name, o.Amount FROM dbo._SfLinT_Orders o JOIN dbo._SfLinT_Customers c ON c.Id = o.CustomerId;");
        await IntegrationDb.ExecuteAsync(cs, """
            CREATE PROCEDURE dbo.usp_SfLinT_BuildMart AS
            BEGIN
                TRUNCATE TABLE dbo._SfLinT_OrderMart;
                INSERT INTO dbo._SfLinT_OrderMart (CustomerId, Total)
                SELECT CustomerId, SUM(Amount) FROM dbo.vw_SfLinT_Summary GROUP BY CustomerId;
            END
            """);
        await IntegrationDb.ExecuteAsync(cs, "CREATE PROCEDURE dbo.usp_SfLinT_Secret WITH ENCRYPTION AS SELECT 1;");
        await IntegrationDb.ExecuteAsync(cs, "CREATE SYNONYM dbo.syn_SfLinT_Orders FOR dbo._SfLinT_Orders;");
    }

    private static Task DropEstateObjects(string cs)
        => IntegrationDb.ExecuteAsync(cs, """
            DROP VIEW IF EXISTS dbo.vw_SfLinT_Summary;
            DROP PROCEDURE IF EXISTS dbo.usp_SfLinT_BuildMart;
            DROP PROCEDURE IF EXISTS dbo.usp_SfLinT_Secret;
            DROP SYNONYM IF EXISTS dbo.syn_SfLinT_Orders;
            DROP TABLE IF EXISTS dbo._SfLinT_Customers, dbo._SfLinT_Orders, dbo._SfLinT_OrderMart;
            """);
}
