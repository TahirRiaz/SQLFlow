using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Collection;
using SqlFlow.Lineage.Graph;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The consumption side of lineage: a <c>subscribers.yaml</c> declares who reads the warehouse, and every
/// subscriber query is PARSED, exactly as a generated view's body is, so the tables and views it names become
/// read edges on the same nodes the loading flows write. That is what makes "which report breaks if I change
/// this table" a graph query. A subscriber is deliberately not a flow: it never runs, so it must never enter
/// the execution plan, the waves, or the estate's flow list.
/// </summary>
public sealed class LineageSubscriberTests : IDisposable
{
    private static readonly DateTime Utc = new(2026, 8, 4, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlflow-sub-" + Guid.NewGuid().ToString("N")[..8]);

    public LineageSubscriberTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, string content)
        => File.WriteAllText(Path.Combine(_root, relative), content);

    private LineageReport Build()
    {
        var collected = new FlowSetCollector().Collect(_root);
        return LineageGraphBuilder.Build(collected, _root, [LineageTier.Declared], Utc);
    }

    private const string Ods = "${env:SQLFLOW_CONN_ODS}";

    /// <summary>An ingestion that writes the table the report below reads: the producing half of the graph.</summary>
    private static string Ingestion => string.Join('\n',
        "flowType: ing",
        "name: citybike_bikes_02_ing",
        "connections:",
        "  pre: ${env:SQLFLOW_CONN_PRE}",
        $"  ods: {Ods}",
        "source:",
        "  server: pre",
        "  object: \"[PreDb].[pre].[v_Bysykkel_Bikes]\"",
        "target:",
        "  server: ods",
        "  object: \"[OdsDb].[arc].[Bysykkel_Bikes]\"",
        "load:",
        "  keyColumns: [id]",
        "schedule:",
        "  cron: \"0 4 * * *\"") + '\n';

    private static string Subscribers(string sql, string type = "PowerBI") => string.Join('\n',
        "connections:",
        $"  dwh: {Ods}",
        "subscribers:",
        "  Analyse_Bysykkel:",
        $"    type: {type}",
        "    owner: analyse@kolumbus.no",
        "    description: City bike usage dashboard",
        "    url: https://app.powerbi.com/groups/me/reports/abc",
        "    server: dwh",
        "    queries:",
        "      - name: Turer",
        "        sql: |",
        "          " + sql) + '\n';

    [Fact]
    public void SubscriberQuery_IsParsed_IntoReadEdgesOnTheProducedTable()
    {
        Write("10_ing.yaml", Ingestion);
        Write("subscribers.yaml", Subscribers("SELECT * FROM [OdsDb].[arc].[Bysykkel_Bikes];"));

        var report = Build();

        var subscriberKey = NodeKey.For(ServerIdentity.Subscriber, null, null, "Analyse_Bysykkel");
        var tableKey = NodeKey.For(Ods, "OdsDb", "arc", "Bysykkel_Bikes");

        // The consumption edge is module-attributed, exactly like a view body's: no flow, ViaModule = the
        // subscriber, so "what reads this table" is one edge query across producers and consumers alike.
        Assert.Contains(report.Edges, e =>
            e.Flow is null && e.ViaModule == subscriberKey
            && e.Relation == LineageRelation.Reads && e.ObjectKey == tableKey);

        // The table the ingestion writes and the table the report reads are ONE node, which is the whole point.
        Assert.Contains(report.Edges, e =>
            e.Flow == "citybike_bikes_02_ing" && e.Relation == LineageRelation.Writes && e.ObjectKey == tableKey);
    }

    [Fact]
    public void Subscriber_BecomesItsOwnNode_WithMetadataAndQueryEvidence()
    {
        Write("10_ing.yaml", Ingestion);
        Write("subscribers.yaml", Subscribers("SELECT * FROM [OdsDb].[arc].[Bysykkel_Bikes];"));

        var report = Build();

        var subscriberKey = NodeKey.For(ServerIdentity.Subscriber, null, null, "Analyse_Bysykkel");
        var node = Assert.Single(report.Objects, o => o.Key == subscriberKey);
        Assert.Equal(LineageNodeKind.Subscriber, node.Kind);
        Assert.Equal("Analyse_Bysykkel", node.Name);

        var subscriber = Assert.Single(report.Subscribers);
        Assert.Equal("Analyse_Bysykkel", subscriber.Name);
        Assert.Equal("PowerBI", subscriber.Type);
        Assert.Equal("analyse@kolumbus.no", subscriber.Owner);
        Assert.Equal("subscribers.yaml", subscriber.File);
        Assert.Equal(subscriberKey, subscriber.ObjectKey);

        // The per-query evidence names the SAME node key the edge points at, so a dossier can say WHICH query
        // links the report to the table rather than only that some query does.
        var query = Assert.Single(subscriber.Queries);
        Assert.Equal("Turer", query.Name);
        Assert.Equal(NodeKey.For(Ods, "OdsDb", "arc", "Bysykkel_Bikes"), Assert.Single(query.ObjectKeys));
    }

    [Fact]
    public void Subscriber_Notes_AreCarriedSeparatelyFromDescription()
    {
        // 'description' says what the report is FOR; 'notes' says what is currently wrong with it. Keeping
        // them apart is the whole point of the second field, so a multi-line remark must survive the loader
        // intact rather than being folded into, or truncated against, the description.
        Write("10_ing.yaml", Ingestion);
        Write("subscribers.yaml", string.Join('\n',
            "connections:",
            $"  dwh: {Ods}",
            "subscribers:",
            "  Analyse_Bysykkel:",
            "    type: PowerBI",
            "    description: City bike usage dashboard",
            "    notes: |",
            "      Inaktivitet. Data oppdatert juni 2022.",
            "      Incomplete dataset. Not resolved in the new warehouse:",
            "        Q_ZoneFra  (Power BI query step)",
            "    server: dwh",
            "    queries:",
            "      - name: Turer",
            "        sql: |",
            "          SELECT * FROM [OdsDb].[arc].[Bysykkel_Bikes];") + '\n');

        var subscriber = Assert.Single(Build().Subscribers);

        Assert.Equal("City bike usage dashboard", subscriber.Description);
        Assert.NotNull(subscriber.Notes);
        Assert.StartsWith("Inaktivitet. Data oppdatert juni 2022.", subscriber.Notes);
        Assert.Contains("Incomplete dataset.", subscriber.Notes);
        Assert.Contains("Q_ZoneFra  (Power BI query step)", subscriber.Notes);
        // The gap named in the note is NOT invented as an edge: notes are prose, and only queries make lineage.
        Assert.Single(subscriber.Queries);
    }

    [Fact]
    public void Subscriber_WithoutNotes_LeavesThemNull()
    {
        Write("10_ing.yaml", Ingestion);
        Write("subscribers.yaml", Subscribers("SELECT * FROM [OdsDb].[arc].[Bysykkel_Bikes];"));

        Assert.Null(Assert.Single(Build().Subscribers).Notes);
    }

    [Fact]
    public void Subscriber_IsNotAFlow_AndNeverEntersTheExecutionPlan()
    {
        Write("10_ing.yaml", Ingestion);
        Write("subscribers.yaml", Subscribers("SELECT * FROM [OdsDb].[arc].[Bysykkel_Bikes];"));

        var report = Build();

        // A subscriber runs nothing. If it leaked into the flow set it would sit in a wave forever pending, and
        // a batch expanding "everything in this repo" would try to execute a Power BI report.
        Assert.DoesNotContain(report.Flows, f => f.Name == "Analyse_Bysykkel");
        Assert.DoesNotContain(report.ExecutionPlan.Waves.SelectMany(w => w.Flows), f => f == "Analyse_Bysykkel");
        Assert.DoesNotContain(report.ExecutionPlan.Unordered, f => f == "Analyse_Bysykkel");

        // Nor does the subscriber library file get reported as an unparseable flow document.
        Assert.DoesNotContain(report.Warnings, w => w.Contains("subscribers.yaml: skipped", StringComparison.Ordinal));
    }

    [Fact]
    public void SubscriberQuery_ReadingAView_LinksToTheViewNodeTheFlowsAlreadyKnow()
    {
        // A report almost never reads a base table directly; it reads the reporting view. The view node must be
        // the same one the ingestion reads, or the consumption side detaches into a parallel graph.
        Write("10_ing.yaml", Ingestion);
        Write("subscribers.yaml", Subscribers("SELECT b.id FROM [PreDb].[pre].[v_Bysykkel_Bikes] AS b;"));

        var report = Build();

        var subscriberKey = NodeKey.For(ServerIdentity.Subscriber, null, null, "Analyse_Bysykkel");
        var viewKey = NodeKey.For(Ods, "PreDb", "pre", "v_Bysykkel_Bikes");
        Assert.Contains(report.Edges, e =>
            e.Flow is null && e.ViaModule == subscriberKey
            && e.Relation == LineageRelation.Reads && e.ObjectKey == viewKey);
    }

    [Fact]
    public void SubscriberQuery_JoinObservations_FeedTheInterpretedDataModel()
    {
        // The joins an analyst writes in a report are real model knowledge, and are collected on the same path a
        // warehouse view's are: a report is evidence of how the business actually relates these tables.
        Write("10_ing.yaml", Ingestion);
        Write("subscribers.yaml", Subscribers(
            "SELECT * FROM [OdsDb].[arc].[Bysykkel_Bikes] b "
            + "INNER JOIN [OdsDb].[arc].[Bysykkel_Stations] s ON s.station_id = b.station_id;"));

        var report = Build();

        var bikes = NodeKey.For(Ods, "OdsDb", "arc", "Bysykkel_Bikes");
        var stations = NodeKey.For(Ods, "OdsDb", "arc", "Bysykkel_Stations");
        Assert.Contains(report.Relationships, r =>
            (r.FromObjectKey == bikes && r.ToObjectKey == stations)
            || (r.FromObjectKey == stations && r.ToObjectKey == bikes));
    }

    [Fact]
    public void Subscriber_WithUndeclaredConnection_IsWarnedAndDropped()
    {
        // The connection alias is what pins a query's two-part names to a server. Without it the objects would
        // land on a made-up identity and quietly build a second, wrong graph, so the query is refused instead.
        Write("subscribers.yaml", string.Join('\n',
            "connections:",
            $"  dwh: {Ods}",
            "subscribers:",
            "  Analyse_Bysykkel:",
            "    type: PowerBI",
            "    server: warehouse",
            "    queries:",
            "      - name: Turer",
            "        sql: SELECT 1 FROM [OdsDb].[arc].[Bysykkel_Bikes];") + '\n');

        var report = Build();

        var subscriber = Assert.Single(report.Subscribers);
        Assert.Empty(subscriber.Queries);
        Assert.Contains(report.Warnings, w =>
            w.Contains("runs against connection 'warehouse'", StringComparison.Ordinal));
    }

    [Fact]
    public void OneFilePerSubscriber_InAFolder_MergesIntoIndependentNodes()
    {
        // The estate convention: a consumer is independently owned, so it gets its own file under
        // subscribers/. Each file is parsed on its own (its own connections block) and they merge into one set
        // of INDEPENDENT nodes, never into a combined one; a report's edges must stay its own.
        Directory.CreateDirectory(Path.Combine(_root, "subscribers"));
        Write("10_ing.yaml", Ingestion);
        Write(Path.Combine("subscribers", "analyse_bysykkel.subscribers.yaml"), string.Join('\n',
            "connections:",
            $"  dwh: {Ods}",
            "subscribers:",
            "  Analyse_Bysykkel:",
            "    type: PowerBI",
            "    server: dwh",
            "    queries:",
            "      - name: Turer",
            "        sql: SELECT * FROM [OdsDb].[arc].[Bysykkel_Bikes];") + '\n');
        Write(Path.Combine("subscribers", "drift_rapport.subscribers.yaml"), string.Join('\n',
            "connections:",
            $"  dwh: {Ods}",
            "subscribers:",
            "  Drift_Rapport:",
            "    type: Excel",
            "    server: dwh",
            "    queries:",
            "      - name: Stasjoner",
            "        sql: SELECT * FROM [OdsDb].[arc].[Bysykkel_Stations];") + '\n');

        var report = Build();

        Assert.Equal(2, report.Subscribers.Count);
        Assert.Equal(
            ["subscribers/analyse_bysykkel.subscribers.yaml", "subscribers/drift_rapport.subscribers.yaml"],
            report.Subscribers.Select(s => s.File).OrderBy(f => f, StringComparer.Ordinal));

        // Two nodes, and each one's read edges belong to it alone.
        var bysykkelKey = NodeKey.For(ServerIdentity.Subscriber, null, null, "Analyse_Bysykkel");
        var driftKey = NodeKey.For(ServerIdentity.Subscriber, null, null, "Drift_Rapport");
        Assert.Contains(report.Objects, o => o.Key == bysykkelKey && o.Kind == LineageNodeKind.Subscriber);
        Assert.Contains(report.Objects, o => o.Key == driftKey && o.Kind == LineageNodeKind.Subscriber);

        var bikes = NodeKey.For(Ods, "OdsDb", "arc", "Bysykkel_Bikes");
        var stations = NodeKey.For(Ods, "OdsDb", "arc", "Bysykkel_Stations");
        Assert.Contains(report.Edges, e => e.ViaModule == bysykkelKey && e.ObjectKey == bikes);
        Assert.DoesNotContain(report.Edges, e => e.ViaModule == bysykkelKey && e.ObjectKey == stations);
        Assert.Contains(report.Edges, e => e.ViaModule == driftKey && e.ObjectKey == stations);
        Assert.DoesNotContain(report.Edges, e => e.ViaModule == driftKey && e.ObjectKey == bikes);

        // A per-subscriber file is still not a flow document.
        Assert.DoesNotContain(report.Flows, f => f.Name is "Analyse_Bysykkel" or "Drift_Rapport");
    }

    [Fact]
    public void Subscriber_DeclaredTwice_KeepsTheFirstAndWarns()
    {
        Write("a.subscribers.yaml", Subscribers("SELECT * FROM [OdsDb].[arc].[Bysykkel_Bikes];"));
        Write("b.subscribers.yaml", Subscribers("SELECT * FROM [OdsDb].[arc].[Other];", type: "Tableau"));

        var report = Build();

        var subscriber = Assert.Single(report.Subscribers);
        Assert.Equal("PowerBI", subscriber.Type);
        Assert.Contains(report.Warnings, w =>
            w.Contains("subscriber 'Analyse_Bysykkel' is already declared", StringComparison.Ordinal));
    }
}
