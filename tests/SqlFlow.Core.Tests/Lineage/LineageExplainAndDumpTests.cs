using System.Text.Json;
using SqlFlow.Core.Lineage;
using SqlFlow.Lineage;
using Xunit;

namespace SqlFlow.Tests.Lineage;

/// <summary>
/// The lineage debugging surfaces: ExplainFlow traces a flow's wave placement (dependencies, reads/writes, tier
/// provenance) purely from the report, and the facts dump exposes the raw pre-merge facts per tier while never
/// echoing an inline connection literal. Offline (declared tier), no database.
/// </summary>
public sealed class LineageExplainAndDumpTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_lineage_dbg_" + Guid.NewGuid().ToString("N"));

    public LineageExplainAndDumpTests() => Directory.CreateDirectory(_dir);

    private void WriteIngFlow(string name, string sourceServer, string sourceObject, string targetServer, string targetObject)
    {
        var connections = sourceServer == targetServer ? $"  {sourceServer}:\n" : $"  {sourceServer}:\n  {targetServer}:\n";
        File.WriteAllText(Path.Combine(_dir, name + ".flow.yaml"), $"""
            flowType: ing
            name: {name}
            connections:
            {connections}source:
              server: {sourceServer}
              object: {sourceObject}
            target:
              server: {targetServer}
              object: {targetObject}
            """);
    }

    private Task<LineageComputation> ComputeAsync()
        => LineageService.ComputeDetailedAsync(new LineageOptions { FlowDirectory = _dir, IncludeObserved = false, IncludeDerived = false });

    [Fact]
    public async Task Explain_TracesAFlowsWavePlacement()
    {
        WriteIngFlow("raw_orders", "SRC", "Src.dbo.Orders", "DW", "DW.raw.Orders");
        WriteIngFlow("dim_customer", "DW", "DW.raw.Orders", "DW", "DW.dim.Customer");

        var report = (await ComputeAsync()).Report;
        var explain = LineageService.ExplainFlow(report, "dim_customer");

        Assert.NotNull(explain);
        Assert.Equal("ing", explain!.Kind);
        Assert.Equal(2, explain.Wave);
        Assert.False(explain.InCycle);

        // It depends on raw_orders (wave 1), mediated by the shared raw.Orders object.
        var dep = Assert.Single(explain.DependsOn);
        Assert.Equal("raw_orders", dep.Flow);
        Assert.Equal(1, dep.Wave);
        Assert.Contains(dep.ViaObjects, o => o.Contains("orders", StringComparison.OrdinalIgnoreCase));

        // Its reads and writes carry the declared tier.
        Assert.Contains(explain.Reads, e => e.ObjectName.Contains("orders", StringComparison.OrdinalIgnoreCase) && e.Tier == LineageTier.Declared);
        Assert.Contains(explain.Writes, e => e.ObjectName.Contains("customer", StringComparison.OrdinalIgnoreCase) && e.Tier == LineageTier.Declared);
    }

    [Fact]
    public async Task Explain_RootFlow_HasNoDependencies()
    {
        WriteIngFlow("raw_orders", "SRC", "Src.dbo.Orders", "DW", "DW.raw.Orders");
        WriteIngFlow("dim_customer", "DW", "DW.raw.Orders", "DW", "DW.dim.Customer");

        var report = (await ComputeAsync()).Report;
        var explain = LineageService.ExplainFlow(report, "raw_orders");

        Assert.NotNull(explain);
        Assert.Equal(1, explain!.Wave);
        Assert.Empty(explain.DependsOn);
        Assert.Contains(explain.RequiredBy, d => string.Equals(d.Flow, "dim_customer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Explain_UnknownFlow_ReturnsNull()
    {
        WriteIngFlow("raw_orders", "SRC", "Src.dbo.Orders", "DW", "DW.raw.Orders");
        var report = (await ComputeAsync()).Report;
        Assert.Null(LineageService.ExplainFlow(report, "no_such_flow"));
    }

    [Fact]
    public async Task FactsDump_CarriesRawFactsWithTierAndProvenance()
    {
        WriteIngFlow("raw_orders", "SRC", "Src.dbo.Orders", "DW", "DW.raw.Orders");

        var facts = (await ComputeAsync()).Facts;

        Assert.Contains(LineageTier.Declared, facts.TiersUsed);
        // The declared write of the target and read of the source are both present, tier-tagged.
        Assert.Contains(facts.Facts, f => f.Relation == LineageRelation.Writes && f.Name.Equals("Orders", StringComparison.OrdinalIgnoreCase)
                                          && f.Schema == "raw" && f.Tier == LineageTier.Declared && f.Flow == "raw_orders");
        Assert.Contains(facts.Facts, f => f.Relation == LineageRelation.Reads && f.Name.Equals("Orders", StringComparison.OrdinalIgnoreCase)
                                          && f.Schema == "dbo" && f.Tier == LineageTier.Declared);
        // Every fact carries its canonical node key for cross-referencing with the report.
        Assert.All(facts.Facts, f => Assert.False(string.IsNullOrEmpty(f.NodeKey)));
    }

    [Fact]
    public async Task FactsDump_RedactsInlineConnectionLiterals()
    {
        // A file flow whose target connection is an inline literal carrying a password.
        File.WriteAllText(Path.Combine(_dir, "f1.flow.yaml"), """
            name: f1
            source:
              type: csv
              location: ./x.csv
            target:
              connection: "Server=db;Database=D;User Id=sa;Password=topsecret;TrustServerCertificate=true"
              schema: dbo
              table: T
            """);

        var facts = (await ComputeAsync()).Facts;
        var serialized = JsonSerializer.Serialize(facts);

        // The secret never reaches the dump; the inline server is shown only by its hashed identity + a redaction.
        Assert.DoesNotContain("topsecret", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(facts.Servers, s => s.Identity.StartsWith("inline:", StringComparison.Ordinal) && s.Reference == "(inline literal redacted)");
    }

    [Fact]
    public async Task FactsDump_KeepsWholeReferenceServersVerbatim()
    {
        WriteIngFlow("raw_orders", "SRC", "Src.dbo.Orders", "DW", "DW.raw.Orders");

        var facts = (await ComputeAsync()).Facts;

        // A bare alias resolves to its conventional ${env:...} reference, which is safe to echo.
        Assert.Contains(facts.Servers, s => s.Reference.StartsWith("${env:", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
