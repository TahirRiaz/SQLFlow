using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Collection;
using SqlFlow.Lineage.Graph;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The generated transform view's declared-tier module lineage, driven by its SQL: the collector synthesizes the
/// same CREATE OR ALTER VIEW the engine executes and attributes what the body reads to the view as a module
/// (flow: null), so the graph carries "table is used in view" (file flow -> table -> view -> downstream) without a
/// live connection. The module key follows the same identity completion the facts get, so a file flow that does
/// not know its target database still lands the module edge on the view node's final, database-qualified key once
/// a downstream flow names it.
/// </summary>
public sealed class LineageGeneratedViewModuleTests : IDisposable
{
    private static readonly DateTime Utc = new(2026, 7, 22, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlflow-viewmod-" + Guid.NewGuid().ToString("N")[..8]);

    public LineageGeneratedViewModuleTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, string content)
        => File.WriteAllText(Path.Combine(_root, relative), content);

    private LineageReport Build()
    {
        var collected = new FlowSetCollector().Collect(_root);
        return LineageGraphBuilder.Build(collected, _root, [LineageTier.Declared], Utc);
    }

    private const string Server = "${env:SQLFLOW_CONN_PRE}";

    private static string FileFlowWithTransform(string name, string table, params string[] columnLines)
    {
        var lines = new List<string>
        {
            $"name: {name}",
            "source:",
            "  type: json",
            "  location: https://acct.dfs.core.windows.net/datalakev2/raw/src/history/",
            "  options: { srcFile: \"*.json\", searchSubDirectories: \"true\" }",
            "target:",
            $"  connection: {Server}",
            "  schema: pre",
            $"  table: {table}",
            "transform:",
            "  generateView: true",
            "  columns:",
        };
        lines.AddRange(columnLines);
        return string.Join('\n', lines) + '\n';
    }

    private static string IngestionReadingView(string name, string sourceObject) => string.Join('\n',
        "flowType: ing",
        $"name: {name}",
        "connections:",
        $"  pre: {Server}",
        "  ods: ${env:SQLFLOW_CONN_ODS}",
        "source:",
        "  server: pre",
        $"  object: \"{sourceObject}\"",
        "target:",
        "  server: ods",
        "  object: \"[OdsDb].[arc].[Trans]\"",
        "load:",
        "  keyColumns: [id]") + '\n';

    [Fact]
    public void GeneratedView_ModuleEdge_ReadsParentTable()
    {
        Write("01_trans.yaml", FileFlowWithTransform("src_trans_01_jsn", "Trans",
            "    - { name: id, expr: \"CAST(@ColName as varchar(50))\" }"));

        var report = Build();

        var viewKey = NodeKey.For(Server, null, "pre", "v_Trans");
        var tableKey = NodeKey.For(Server, null, "pre", "Trans");
        // The module edge is flow-less: the view body (its synthesized SQL) reads the parent table.
        Assert.Contains(report.Edges, e =>
            e.Flow is null && e.ViaModule == viewKey && e.Relation == LineageRelation.Reads && e.ObjectKey == tableKey);
        // No self-fact: the CREATE VIEW statement pointing at the view itself carries nothing.
        Assert.DoesNotContain(report.Edges, e => e.ViaModule == viewKey && e.ObjectKey == viewKey);
    }

    [Fact]
    public void GeneratedView_ModuleEdge_IncludesTableReferencedInExpression()
    {
        // An authored expression that references another table: the view's SQL names it, so the module reads it
        // too; the link is driven by the code, not by an assumption that only the FROM table participates.
        Write("01_trans.yaml", FileFlowWithTransform("src_trans_01_jsn", "Trans",
            "    - { name: id, expr: \"CAST(@ColName as varchar(50))\" }",
            "    - { name: currency, expr: \"(SELECT TOP 1 [Code] FROM [ref].[CurrencyMap] WHERE [ref].[CurrencyMap].[Id] = @ColName)\" }"));

        var report = Build();

        var viewKey = NodeKey.For(Server, null, "pre", "v_Trans");
        Assert.Contains(report.Edges, e =>
            e.Flow is null && e.ViaModule == viewKey && e.Relation == LineageRelation.Reads
            && e.ObjectKey == NodeKey.For(Server, null, "ref", "CurrencyMap"));
    }

    [Fact]
    public void GeneratedView_ModuleKey_CompletesViaServerDefaultDatabase()
    {
        // In a connected or env-resolved run the server's default catalog is known and every fact's database is
        // completed from it. The module key is case-folded when the collector builds it, so it must complete
        // through the same map regardless of the reference's casing, or the module edge detaches from the
        // database-qualified view node (the estate symptom: view floating next to its table).
        Write("01_trans.yaml", FileFlowWithTransform("src_trans_01_jsn", "Trans",
            "    - { name: id, expr: \"CAST(@ColName as varchar(50))\" }"));

        var collected = new FlowSetCollector().Collect(_root);
        collected.ServerDefaultDatabases[Server] = "PreDb";
        var report = LineageGraphBuilder.Build(collected, _root, [LineageTier.Declared], Utc);

        var viewKey = NodeKey.For(Server, "PreDb", "pre", "v_Trans");
        var tableKey = NodeKey.For(Server, "PreDb", "pre", "Trans");
        Assert.Contains(report.Edges, e =>
            e.Flow is null && e.ViaModule == viewKey && e.Relation == LineageRelation.Reads && e.ObjectKey == tableKey);
    }

    [Fact]
    public void GeneratedView_ModuleKey_CompletesToDatabaseQualifiedViewNode()
    {
        // The downstream ing flow reads the view three-part, so identity unification lands the view node on the
        // database-qualified key; the module key must follow it there or the GUI cannot pair the module edge with
        // the view node ("table is used in view" would detach again).
        Write("01_trans.yaml", FileFlowWithTransform("src_trans_01_jsn", "Trans",
            "    - { name: id, expr: \"CAST(@ColName as varchar(50))\" }"));
        Write("02_ing.yaml", IngestionReadingView("src_trans_02_ing", "[PreDb].[pre].[v_Trans]"));

        var report = Build();

        var viewKey = NodeKey.For(Server, "PreDb", "pre", "v_Trans");
        Assert.Contains(report.Objects, o => o.Key == viewKey);
        Assert.Contains(report.Edges, e => e.Flow is null && e.ViaModule == viewKey && e.Relation == LineageRelation.Reads);

        // The physical chain still schedules: the load runs before the merge that reads its view.
        var loadWave = report.ExecutionPlan.Waves.Single(w => w.Flows.Contains("src_trans_01_jsn")).Wave;
        var mergeWave = report.ExecutionPlan.Waves.Single(w => w.Flows.Contains("src_trans_02_ing")).Wave;
        Assert.True(loadWave < mergeWave);
    }
}
