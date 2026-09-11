using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The deployment manifests are part of the product's contract with the estate: a worker tier that speaks only the
/// node protocol must carry no catalog credential, and the autoscaler must read the control plane's own replica
/// target with the node token rather than query the catalog with a credential of its own. These tests read the
/// manifests from the repository, so a regression that quietly reintroduces a catalog connection on the worker
/// tier, or the old SQL scaler, fails here before it ships.
/// </summary>
public sealed class DeployManifestTests
{
    [Fact]
    public void WorkerManifests_CarryNoCatalogConnection()
    {
        foreach (var path in new[] { "deploy/bicep/worker.bicep", "deploy/k8s/worker-pool.yaml", "Dockerfile.worker", "deploy/docker/worker-entrypoint.sh" })
        {
            Assert.DoesNotContain("SQLFLOW_CATALOG_DB", Manifest(path), StringComparison.Ordinal);
        }

        // The compose file also hosts the control plane, which does read the catalog: only the worker service is bound.
        var compose = Manifest("deploy/compose/docker-compose.yml");
        var workerStart = compose.IndexOf("  worker:", StringComparison.Ordinal);
        var workerEnd = compose.IndexOf("  gui:", StringComparison.Ordinal);
        Assert.True(workerStart >= 0 && workerEnd > workerStart, "the compose file no longer has a worker service ahead of the gui service");
        Assert.DoesNotContain("SQLFLOW_CATALOG_DB", compose[workerStart..workerEnd], StringComparison.Ordinal);
    }

    [Fact]
    public void Scalers_ReadTheControlPlanesTarget_WithTheNodeToken_NotTheCatalog()
    {
        var bicep = Manifest("deploy/bicep/worker.bicep");
        Assert.Contains("type: 'metrics-api'", bicep, StringComparison.Ordinal);
        Assert.Contains("/api/v1/node/scale-target?pool=", bicep, StringComparison.Ordinal);
        Assert.Contains("triggerParameter: 'token'", bicep, StringComparison.Ordinal);
        Assert.DoesNotContain("mssql", bicep, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("catalog-scaler", bicep, StringComparison.Ordinal);
        Assert.DoesNotContain("maxConcurrentRunsPerReplica", bicep, StringComparison.Ordinal);

        var yaml = Manifest("deploy/k8s/worker-pool.yaml");
        Assert.Contains("type: metrics-api", yaml, StringComparison.Ordinal);
        Assert.Contains("/api/v1/node/scale-target?pool=", yaml, StringComparison.Ordinal);
        Assert.Contains("parameter: token", yaml, StringComparison.Ordinal);
        Assert.Contains("key: node-token", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("mssql", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("catalog-connection", yaml, StringComparison.Ordinal);

        var main = Manifest("deploy/bicep/main.bicep");
        Assert.DoesNotContain("sqlflow-catalog-db-scaler", main, StringComparison.Ordinal);
        Assert.DoesNotContain("catalogScalerUrl", main, StringComparison.Ordinal);
    }

    private static string Manifest(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>The repository root, found by walking up from the test binaries to the solution file, so the tests
    /// read the manifests as committed rather than a copy that could drift.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SqlFlow.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir.FullName;
    }
}
