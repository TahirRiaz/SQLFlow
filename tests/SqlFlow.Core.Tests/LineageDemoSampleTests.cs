using System.Runtime.CompilerServices;
using SqlFlow.Core.Secrets;
using SqlFlow.Lineage;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The <c>samples/lineage-demo</c> estate as the reference pages describe it: each landing flow refreshes the typed view
/// <c>v_&lt;Table&gt;</c> over its pre table, each ingestion flow reads that view, and the offline (declared) tier alone
/// orders every ingestion flow after the landing flow that writes its view. The ingestion flows once named the view by
/// an earlier form (<c>vOrders_Pre</c>) the engine no longer creates, so they read an object nothing wrote and ran in
/// the first wave beside the flows they depend on.
/// </summary>
public sealed class LineageDemoSampleTests
{
    [Fact]
    public async Task EveryIngestionFlow_ReadsTheViewItsLandingFlowWrites_AndRunsAfterIt()
    {
        var report = await LineageService.ComputeAsync(new LineageOptions
        {
            FlowDirectory = DemoDirectory(),
            IncludeObserved = false,
            // The connection reference names the demo database offline (its Initial Catalog), so the landing flow's
            // two-part target and the ingestion flow's three-part source resolve to one identity. A resolver of its
            // own keeps the test off the process environment.
            Secrets = new DemoSecrets(),
        });

        var waveOf = report.ExecutionPlan.Waves
            .SelectMany(w => w.Flows.Select(f => (Flow: f, w.Wave)))
            .ToDictionary(p => p.Flow, p => p.Wave, StringComparer.OrdinalIgnoreCase);

        foreach (var (landing, ingestion, view) in new[]
                 {
                     ("demo-land-orders", "demo-ing-orders", "v_orders_pre"),
                     ("demo-land-customers", "demo-ing-customers", "v_customers_pre"),
                 })
        {
            Assert.True(waveOf[ingestion] > waveOf[landing], $"{ingestion} must run after {landing}.");
            var dependency = Assert.Single(report.FlowDependencies, d =>
                string.Equals(d.FromFlow, landing, StringComparison.OrdinalIgnoreCase)
                && string.Equals(d.ToFlow, ingestion, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(dependency.ViaObjects, o => o.EndsWith($"|demo|{view}", StringComparison.Ordinal));
        }

        Assert.DoesNotContain(report.Objects, o => o.Key.EndsWith("|demo|vorders_pre", StringComparison.Ordinal)
                                                   || o.Key.EndsWith("|demo|vcustomers_pre", StringComparison.Ordinal));
    }

    /// <summary>The demo folder, found from the test binaries (a build inside the repository) or, when they were built
    /// elsewhere (an artifacts folder on another drive), from this source file's own location.</summary>
    private static string DemoDirectory([CallerFilePath] string sourceFile = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(sourceFile) })
        {
            for (var dir = string.IsNullOrEmpty(start) ? null : new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "samples", "lineage-demo");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate 'samples/lineage-demo' above the test output ('{AppContext.BaseDirectory}') or the test source ('{sourceFile}').");
    }

    /// <summary>Resolves the demo's one connection reference to a connection string naming its database; nothing
    /// connects to it, since the offline tiers only read its Initial Catalog.</summary>
    private sealed class DemoSecrets : ISecretResolver
    {
        private const string Demo = "Server=localhost;Database=SqlFlowCatalogTests;Integrated Security=True;TrustServerCertificate=True";

        public string Resolve(string value)
            => string.Equals(value, "${env:SQLFLOW_DEMO_DB}", StringComparison.Ordinal) ? Demo : value;

        public Task<string> ResolveAsync(string value, CancellationToken ct = default) => Task.FromResult(Resolve(value));
    }
}
