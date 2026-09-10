using SqlFlow.HealthCheck;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>The model state, file-based and ephemeral: a save/load round trip preserves model bytes and
/// metadata (including the trend the scoring needs), a missing model is null (train), per-metric folders keep
/// a flow's models apart, and a model whose metadata sidecar was tampered away is a clear error.</summary>
public sealed class HealthCheckModelStoreTests : IDisposable
{
    private readonly string _anchor = Path.Combine(Path.GetTempPath(), "sqlflow-hc-store-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_anchor))
        {
            Directory.Delete(_anchor, recursive: true);
        }
    }

    private static HealthCheckModelMetadata Metadata(string flowName) => new()
    {
        EngineVersion = HealthCheckModelMetadata.CurrentEngineVersion,
        FlowName = flowName,
        Trainer = "FastTreeRegression",
        TrainedAtUtc = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc),
        TrainingSeconds = 42.5,
        Trials = 7,
        ValidationRSquared = 0.93,
        TrendAnchorDate = new DateTime(2026, 1, 1),
        TrendSlopePerDay = 1.25,
        TrendIntercept = 980.5,
        TrendWindowPoints = 162,
        TrialResults =
        [
            new HealthCheckTrialResult { Trainer = "FastTreeRegression", RSquared = 0.93, RuntimeSeconds = 3.2 },
            new HealthCheckTrialResult { Trainer = "LbfgsPoissonRegression", RSquared = 0.71, RuntimeSeconds = 1.1 },
        ],
    };

    [Fact]
    public void SaveThenLoad_RoundTripsModelAndMetadata()
    {
        var store = new HealthCheckModelStore(_anchor);
        var bytes = new byte[] { 1, 2, 3, 4, 5 };

        var path = store.Save("orders-watch", "rowCount", bytes, Metadata("orders-watch"));
        var loaded = store.Load("orders-watch", "rowCount");

        Assert.Equal(store.ModelPath("orders-watch", "rowCount"), path);
        Assert.NotNull(loaded);
        Assert.Equal(bytes, loaded.ModelBytes);
        Assert.Equal("FastTreeRegression", loaded.Metadata.Trainer);
        Assert.Equal(HealthCheckModelMetadata.CurrentEngineVersion, loaded.Metadata.EngineVersion);
        Assert.Equal(7, loaded.Metadata.Trials);
        Assert.Equal(0.93, loaded.Metadata.ValidationRSquared);
        Assert.Equal(1.25, loaded.Metadata.TrendSlopePerDay);
        Assert.Equal(980.5, loaded.Metadata.TrendIntercept);
        Assert.Equal(new DateTime(2026, 1, 1), loaded.Metadata.TrendAnchorDate);
        Assert.Equal(162, loaded.Metadata.TrendWindowPoints);
        Assert.Equal(2, loaded.Metadata.TrialResults.Count);
    }

    [Fact]
    public void Load_MissingModel_IsNull()
    {
        Assert.Null(new HealthCheckModelStore(_anchor).Load("never-trained", "rowCount"));
    }

    [Fact]
    public void Metrics_KeepSeparateModels()
    {
        var store = new HealthCheckModelStore(_anchor);
        store.Save("flow", "rowCount", [1], Metadata("flow"));
        store.Save("flow", "revenue", [2, 2], Metadata("flow") with { Trainer = "LightGbmRegression" });

        Assert.Equal([1], store.Load("flow", "rowCount")!.ModelBytes);
        Assert.Equal([2, 2], store.Load("flow", "revenue")!.ModelBytes);
        Assert.Equal("LightGbmRegression", store.Load("flow", "revenue")!.Metadata.Trainer);
    }

    [Fact]
    public void Save_OverwritesThePriorModel()
    {
        var store = new HealthCheckModelStore(_anchor);
        store.Save("flow", "m", [1], Metadata("flow"));
        store.Save("flow", "m", [9, 9], Metadata("flow") with { Trainer = "LightGbmRegression" });

        var loaded = store.Load("flow", "m");

        Assert.NotNull(loaded);
        Assert.Equal([9, 9], loaded.ModelBytes);
        Assert.Equal("LightGbmRegression", loaded.Metadata.Trainer);
    }

    [Fact]
    public void Load_ModelWithoutSidecar_FailsWithTheRecoverySteps()
    {
        var store = new HealthCheckModelStore(_anchor);
        store.Save("flow", "m", [1, 2], Metadata("flow"));
        File.Delete(Path.Combine(store.StateDirectory("flow", "m"), "healthcheck.model.json"));

        var ex = Assert.Throws<InvalidOperationException>(() => store.Load("flow", "m"));
        Assert.Contains("metadata sidecar", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Delete the folder", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StateDirectory_UsesTheSharedSafeNameRule()
    {
        var store = new HealthCheckModelStore(_anchor);

        var directory = store.StateDirectory("bad/name:check", "row*count");

        Assert.EndsWith(Path.Combine(".sqlflow", "state", "bad_name_check", "row_count"), directory, StringComparison.Ordinal);
    }

    [Fact]
    public void EphemeralStore_RoundTrips_AndStartsEmpty()
    {
        var store = new EphemeralHealthCheckModelStore();
        Assert.Null(store.Load("f", "m"));

        store.Save("f", "m", [7], Metadata("f"));
        var loaded = store.Load("f", "m");

        Assert.NotNull(loaded);
        Assert.Equal([7], loaded.ModelBytes);
        Assert.Null(store.Load("f", "other"));
    }
}
