using SqlFlow.Core.Lineage;
using SqlFlow.Lineage;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Offline end-to-end through the real service over real estates on disk: the harness writes documents and
/// canonical run artifacts, the service computes, the tests pin the resulting PLAN: the contract the batch
/// runner will consume. This is the layer that proves the tiers compose, not just that each part works.
/// </summary>
public sealed class LineageServiceTests
{
    [Fact]
    public async Task DeclaredEstate_PlansTheMedallionOrder()
    {
        using var estate = new LineageEstateHarness()
            .Flow("bronze/load-customers.flow.yaml", LineageEstateHarness.Ingestion("load-customers", "Staging.dbo.Customers", "DW.dbo.Customers"))
            .Flow("bronze/load-orders.flow.yaml", LineageEstateHarness.Ingestion("load-orders", "Staging.dbo.Orders", "DW.dbo.Orders"))
            .Flow("silver/build-facts.flow.yaml", LineageEstateHarness.Ingestion("build-facts", "DW.dbo.Orders", "DW.dbo.FactOrders", sourceRef: "${env:SQLFLOW_CONN_DWH}"))
            .Flow("gold/watch.flow.yaml", LineageEstateHarness.HealthCheck("orders-watch", "DW.dbo.FactOrders", "OrderDate"));

        var report = await estate.ComputeAsync();

        Assert.Equal(
            [["load-customers", "load-orders"], ["build-facts"], ["orders-watch"]],
            LineageEstateHarness.Waves(report));
        Assert.Empty(report.Cycles);
        Assert.Equal([LineageTier.Declared, LineageTier.Observed], report.TiersUsed);
    }

    [Fact]
    public async Task ObservedTier_RevealsWhatTheYamlDoesNot()
    {
        // The post-process hook truth lives only in the executed trace: a side table written at run time.
        // The observed tier connects its reader; without it the reader floats free.
        using var estate = new LineageEstateHarness()
            .Flow("load-orders.flow.yaml", LineageEstateHarness.Ingestion("load-orders", "Staging.dbo.Orders", "DW.dbo.Orders"))
            .Flow("audit-watch.flow.yaml", LineageEstateHarness.HealthCheck("audit-watch", "DW.audit.LoadLog", "At"))
            .Run("load-orders", new DateTime(2026, 6, 10, 6, 0, 0, DateTimeKind.Utc),
                ("source.select", "SELECT * FROM [Staging].[dbo].[Orders];"),
                ("staging.create", "CREATE TABLE [DW].[dbo].[stg_orders_99] (Id int);"),
                ("upsert.insert", "INSERT INTO [DW].[dbo].[Orders] SELECT * FROM [DW].[dbo].[stg_orders_99];"),
                ("staging.drop", "DROP TABLE [DW].[dbo].[stg_orders_99];"),
                ("postprocess", "INSERT INTO [DW].[audit].[LoadLog] (At) SELECT GETUTCDATE();"));

        var withObserved = await estate.ComputeAsync(includeObserved: true);
        var withoutObserved = await estate.ComputeAsync(includeObserved: false);

        Assert.Equal([["load-orders"], ["audit-watch"]], LineageEstateHarness.Waves(withObserved));
        Assert.Equal([["audit-watch", "load-orders"]], LineageEstateHarness.Waves(withoutObserved));

        // The transient staging table never reaches the graph; the observed edges carry their run stamp.
        Assert.DoesNotContain(withObserved.Objects, o => o.Name.Contains("stg_", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(withObserved.Edges, e => e.Tier == LineageTier.Observed && e.ObservedRunId is not null);
    }

    [Fact]
    public async Task ObservedTier_CapturesObjectScriptAndColumns()
    {
        // The engine's generated trace creates the target and loads it. The observed tier should attach the
        // generating script and an offline column dictionary to the target object, without a live connection:
        // the accurate context an LLM needs to author a query against it.
        using var estate = new LineageEstateHarness()
            .Flow("load-orders.flow.yaml", LineageEstateHarness.Ingestion("load-orders", "Staging.dbo.Orders", "DW.dbo.Orders"))
            .Run("load-orders", new DateTime(2026, 6, 10, 6, 0, 0, DateTimeKind.Utc),
                ("target.evolve", "CREATE TABLE [DW].[dbo].[Orders] ([Id] int NOT NULL, [Amount] decimal(18,2) NULL);"),
                ("upsert.insert", "INSERT INTO [DW].[dbo].[Orders] ([Id], [Amount]) SELECT s.[Id], s.[Amount] FROM [DW].[dbo].[OrdersSource] s;"));

        var report = await estate.ComputeAsync();

        // The target object carries its generating script and columns, both from the observed tier.
        var orders = report.Objects.Single(o => o.Key.EndsWith("|dw|dbo|orders", StringComparison.Ordinal));
        Assert.NotNull(orders.Script);
        Assert.Contains("CREATE TABLE", orders.Script!, StringComparison.Ordinal);
        Assert.Equal(LineageTier.Observed, orders.ScriptTier);
        Assert.Equal(LineageTier.Observed, orders.ColumnsTier);
        Assert.Collection(orders.Columns.OrderBy(c => c.Ordinal),
            c => { Assert.Equal("Id", c.Name); Assert.False(c.Nullable); },
            c => { Assert.Equal("Amount", c.Name); Assert.True(c.Nullable); });
    }

    [Fact]
    public async Task StaleDocument_IsWarnedInTheReport()
    {
        using var estate = new LineageEstateHarness()
            .Flow("load-orders.flow.yaml",
                LineageEstateHarness.Ingestion("load-orders", "Staging.dbo.Orders", "DW.dbo.Orders"),
                writeUtc: new DateTime(2026, 6, 11, 0, 0, 0, DateTimeKind.Utc))
            .Run("load-orders", new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                ("upsert.insert", "INSERT INTO [DW].[dbo].[Orders] SELECT 1;"));

        var report = await estate.ComputeAsync();

        Assert.Contains(report.Warnings, w => w.Contains("changed after its last run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FileFlowTarget_GainsItsDatabase_FromTheConnectionsInitialCatalog()
    {
        // A file flow declares no database anywhere; the connection string owns it. The lineage identity
        // must still carry it, offline, from the resolved reference's Initial Catalog.
        var variable = "SQLFLOW_TEST_SINK_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        Environment.SetEnvironmentVariable(variable, "Server=localhost;Initial Catalog=Landing;Integrated Security=true;");
        try
        {
            using var estate = new LineageEstateHarness().Flow("csv.flow.yaml", $$"""
                name: csv-load
                source: { type: csv, location: ./data/orders.csv }
                target:
                  connection: ${env:{{variable}}}
                  schema: raw
                  table: Orders
                """);

            var report = await estate.ComputeAsync();

            var node = Assert.Single(report.Objects, o => o.Name == "Orders");
            Assert.Equal("Landing", node.Database, ignoreCase: true); // node metadata carries the case-folded key part
            Assert.Contains("|landing|raw|orders", node.Key, StringComparison.Ordinal);
            Assert.DoesNotContain(report.Warnings, w => w.Contains("no database identity", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task FileFlowTarget_WarnsAndStaysDatabaseless_WhenTheReferenceCannotResolve()
    {
        // Offline with an unresolvable reference the identity cannot be completed; the report must name
        // both the unknown default database and the incomplete identity instead of degrading silently.
        using var estate = new LineageEstateHarness().Flow("csv.flow.yaml", """
            name: csv-load
            source: { type: csv, location: ./data/orders.csv }
            target:
              connection: ${env:SQLFLOW_TEST_SINK_THAT_IS_NEVER_SET}
              schema: raw
              table: Orders
            """);

        var report = await estate.ComputeAsync();

        var node = Assert.Single(report.Objects, o => o.Name == "Orders");
        Assert.Null(node.Database);
        Assert.Contains(report.Warnings, w => w.Contains("default database unknown", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.Contains("no database identity", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HookLineage_OrdersTheHookTargetsReaders()
    {
        using var estate = new LineageEstateHarness()
            .Flow("load.flow.yaml", LineageEstateHarness.Ingestion(
                "load-orders", "Staging.dbo.Orders", "DW.dbo.Orders",
                postProcess: "INSERT INTO DW.audit.Stamps (At) SELECT GETUTCDATE();"))
            .Flow("stamp-export.flow.yaml", LineageEstateHarness.Export("stamp-export", "DW.audit.Stamps"));

        var report = await estate.ComputeAsync();

        Assert.Equal([["load-orders"], ["stamp-export"]], LineageEstateHarness.Waves(report));
    }

    [Fact]
    public async Task ImpactAndDependencyQueries_WalkTheWholeChain()
    {
        using var estate = new LineageEstateHarness()
            .Flow("load.flow.yaml", LineageEstateHarness.Ingestion("load-orders", "Staging.dbo.Orders", "DW.dbo.Orders"))
            .Flow("facts.flow.yaml", LineageEstateHarness.Ingestion("build-facts", "DW.dbo.Orders", "DW.dbo.FactOrders", sourceRef: "${env:SQLFLOW_CONN_DWH}"))
            .Flow("export.flow.yaml", LineageEstateHarness.Export("fact-export", "DW.dbo.FactOrders"));

        var report = await estate.ComputeAsync();

        var subject = Assert.Single(LineageService.ResolveSubject(report, "DW.dbo.Orders"));
        var downstream = LineageService.Downstream(report, subject);
        Assert.Contains("flow:build-facts", downstream);
        Assert.Contains("flow:fact-export", downstream);
        Assert.Contains(downstream, n => n.EndsWith("|dw|dbo|factorders", StringComparison.Ordinal));

        var upstreamOfExport = LineageService.Upstream(report, "fact-export");
        Assert.Contains("flow:build-facts", upstreamOfExport);
        Assert.Contains(upstreamOfExport, n => n.EndsWith("|dw|dbo|orders", StringComparison.Ordinal));
        Assert.Contains("flow:load-orders", upstreamOfExport);
    }

    [Fact]
    public async Task ResolveSubject_FlowNamesObjectsAndAmbiguity()
    {
        using var estate = new LineageEstateHarness()
            .Flow("a.flow.yaml", LineageEstateHarness.Ingestion("load-a", "SrcA.dbo.Orders", "DwA.dbo.Orders"))
            .Flow("b.flow.yaml", LineageEstateHarness.Ingestion("load-b", "SrcB.dbo.Orders", "DwB.dbo.Orders"));

        var report = await estate.ComputeAsync();

        Assert.Equal(["flow:load-a"], LineageService.ResolveSubject(report, "load-a"));
        Assert.Single(LineageService.ResolveSubject(report, "DwA.dbo.Orders"));

        // A bare name matching several identities comes back as ALL of them, never a silent pick.
        Assert.True(LineageService.ResolveSubject(report, "Orders").Count >= 4);
        Assert.Empty(LineageService.ResolveSubject(report, "NoSuchThing"));
    }

    [Fact]
    public async Task ServiceOutput_IsByteDeterministic()
    {
        using var estate = new LineageEstateHarness()
            .Flow("z.flow.yaml", LineageEstateHarness.Ingestion("zeta", "S.dbo.A", "DW.dbo.B"))
            .Flow("a.flow.yaml", LineageEstateHarness.Ingestion("alpha", "DW.dbo.B", "DW.dbo.C", sourceRef: "${env:SQLFLOW_CONN_DWH}"))
            .Run("zeta", new DateTime(2026, 6, 10, 6, 0, 0, DateTimeKind.Utc),
                ("upsert.insert", "INSERT INTO [DW].[dbo].[B] SELECT * FROM [S].[dbo].[A];"));

        var first = await estate.ComputeAsync();
        var second = await estate.ComputeAsync();

        // Everything except the generation timestamp must be identical.
        var json1 = System.Text.Json.JsonSerializer.Serialize(first with { GeneratedAtUtc = default });
        var json2 = System.Text.Json.JsonSerializer.Serialize(second with { GeneratedAtUtc = default });
        Assert.Equal(json1, json2);
    }

    [Fact]
    public async Task EmptyEstate_PlansNothing_Gracefully()
    {
        using var estate = new LineageEstateHarness();

        var report = await estate.ComputeAsync();

        Assert.Empty(report.Flows);
        Assert.Empty(report.ExecutionPlan.Waves);
        Assert.Empty(report.Cycles);
    }
}
