using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Runs;

namespace SqlFlow.Core.HealthChecks;

/// <summary>When the health check trains a fresh anomaly model versus reusing the one persisted in the
/// flow's state folder.</summary>
public enum HealthCheckTraining
{
    /// <summary>Train when no stored model exists, the stored model is older than
    /// <see cref="HealthCheckFlow.RetrainAfterDays"/>, or it was trained by an older detection engine;
    /// otherwise reuse it. The default.</summary>
    Auto = 0,

    /// <summary>Train a fresh model on every run (the legacy RunModelSelection=1 behavior).</summary>
    Always = 1,

    /// <summary>Never train: score with the stored model and fail clearly when none exists.</summary>
    Never = 2,
}

/// <summary>One monitored metric of a health-check flow: a named aggregate over the target table.</summary>
public sealed record HealthCheckMetric
{
    /// <summary>The metric's name: keys the trained model's state folder and the report section.</summary>
    public required string Name { get; init; }

    /// <summary>The aggregate T-SQL expression producing the value per date: <c>COUNT(*)</c>,
    /// <c>SUM(Amount)</c>, <c>COUNT(DISTINCT CustomerID)</c>, <c>AVG(UnitPrice)</c>. A metric-specific
    /// condition is written into the aggregate itself (<c>SUM(CASE WHEN Status = 'OK' THEN 1 END)</c>);
    /// all metrics share the flow-level filter and one table scan.</summary>
    public required string Expression { get; init; }

    /// <summary>The conventional name when the author gives none: <c>rowCount</c> for a plain
    /// <c>COUNT(*)</c>, <c>value</c> otherwise.</summary>
    public static string DefaultName(string expression)
        => string.Equals(expression.Trim().Replace(" ", string.Empty, StringComparison.Ordinal), "COUNT(*)", StringComparison.OrdinalIgnoreCase)
            ? "rowCount"
            : "value";
}

/// <summary>
/// An ML health-check flow (legacy flw.HealthCheck, FlowType 'hc'): learns the expected behavior of one or
/// more per-date metrics on a monitored SQL Server table and tags the dates whose actual values deviate
/// significantly from the prediction. The detection stack is the published canon: a Theil-Sen robust trend, an
/// AutoML calendar model over the detrended series, generalized-ESD point anomalies with median/MAD
/// studentization, PELT level-shift detection, and a weekday-median baseline for short histories, plus
/// table-level data-quality probes (future dates, sentinel dates, NULL dates). The monitored object is
/// addressed by a three-part name and the server by a connection name, exactly like the other relational flow
/// kinds. Trained models live in the flow's <c>.sqlflow/state</c> folder, one per metric; the scored series is
/// the run's canonical <c>healthcheck.json</c> artifact. Immutable.
/// </summary>
public sealed record HealthCheckFlow
{
    /// <summary>The stable name-derived identity (the legacy FlowID role).</summary>
    public int FlowId { get; init; }

    public string? Batch { get; init; }

    /// <summary>The flow's declared lifecycle (the YAML <c>lifecycle:</c>, production by default): a development
    /// flow runs exactly like a production one but never generates notification events.</summary>
    public Runs.FlowLifecycle Lifecycle { get; init; } = Runs.FlowLifecycle.Production;

    public required string SysAlias { get; init; }

    /// <summary>The connection name the monitored table is read through (legacy trgServer).</summary>
    public required string Server { get; init; }

    /// <summary>The monitored three-part object (legacy flw.HealthCheck.DBSchTbl).</summary>
    public required RelationalObject Target { get; init; }

    /// <summary>The date column the metrics are grouped by (legacy DateColumn).</summary>
    public required string DateColumn { get; init; }

    /// <summary>The monitored metrics, all computed in one scan of the target (at least one).</summary>
    public required IReadOnlyList<HealthCheckMetric> Metrics { get; init; }

    /// <summary>An optional boolean T-SQL expression ANDed into the series query (legacy FilterCriteria);
    /// shared by every metric.</summary>
    public string? FilterCriteria { get; init; }

    /// <summary>The AutoML experiment budget in seconds, per metric (legacy MLMaxExperimentTimeInSeconds,
    /// default 120).</summary>
    public int MaxExperimentSeconds { get; init; } = 120;

    /// <summary>The minimum severity (robust sigmas of prediction error) a statistically significant point
    /// must still reach to be reported. The operational guard on top of the ESD significance test.</summary>
    public double AnomalyThreshold { get; init; } = 2.0;

    /// <summary>The significance level of the generalized ESD test: the probability of a clean series
    /// producing a deviation this extreme. Lower means fewer false alarms.</summary>
    public double EsdAlpha { get; init; } = 0.025;

    /// <summary>The ESD search bound: at most this share of the series can be flagged as point anomalies.</summary>
    public double MaxAnomalyFraction { get; init; } = 0.10;

    /// <summary>Trailing days whose data may still be arriving: scored and reported but never counted as
    /// anomalies. 1 keeps today's partial load from paging anyone; 0 disables the window.</summary>
    public int MaturityDays { get; init; } = 1;

    /// <summary>Rows dated before this are counted as sentinel-date rows by the data-quality probe (the
    /// 1900-01-01 placeholders that creep into warehouse date columns).</summary>
    public DateOnly SentinelDateFloor { get; init; } = new(1990, 1, 1);

    /// <summary>When the health check executes: <see cref="ExecutionMode.Auto"/> (the default) lets schedules
    /// and batch/node group runs pick the flow up like any other; <see cref="ExecutionMode.Manual"/> excludes it
    /// from every automatic dispatch (the scheduler, group expansion, local batch membership), so it runs only
    /// when triggered directly (the GUI's run button, a single-flow API trigger, or a direct CLI run).</summary>
    public ExecutionMode Mode { get; init; } = ExecutionMode.Auto;

    public HealthCheckTraining Training { get; init; } = HealthCheckTraining.Auto;

    /// <summary>With <see cref="HealthCheckTraining.Auto"/>: a stored model older than this many days is
    /// retrained. Null keeps the stored model until it is deleted, outdated by the engine, or training is
    /// forced.</summary>
    public int? RetrainAfterDays { get; init; }

    /// <summary>Dates treated as holidays in the calendar features (the without-database replacement for the
    /// legacy flw.SysDateFeatures.IsHoliday column).</summary>
    public IReadOnlyList<DateOnly> Holidays { get; init; } = [];

    public string? Description { get; init; }

    public string FlowType { get; init; } = "hc";

    /// <summary>The connection reference understood by the connection resolver.</summary>
    public string ConnectionReference => "@" + Server;
}
