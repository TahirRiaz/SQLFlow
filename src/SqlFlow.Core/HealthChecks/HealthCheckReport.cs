namespace SqlFlow.Core.HealthChecks;

/// <summary>
/// The canonical scored-series report of one health-check run: the <c>healthcheck.json</c> artifact in the
/// .sqlflow run history. Like <see cref="Runs.RunArtifact"/>, the envelope is a stable, versioned contract so
/// the flat files can be bulk-loaded or charted without per-run parsing; the day-by-day detail rides in each
/// metric's <see cref="HealthCheckMetricReport.Series"/>.
/// </summary>
public sealed record HealthCheckReport
{
    /// <summary>The current healthcheck.json schema version written by this build. Version 2: multi-metric,
    /// severities, level shifts, trend, maturity, data quality.</summary>
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string FlowName { get; init; }

    public required Guid RunId { get; init; }

    public required DateTime WrittenUtc { get; init; }

    /// <summary>The monitored object's bracketed three-part name.</summary>
    public required string Target { get; init; }

    public required string DateColumn { get; init; }

    public string? FilterCriteria { get; init; }

    /// <summary>The cadence detected from the shared date axis (Daily, Weekly, Monthly, ...).</summary>
    public required string Frequency { get; init; }

    /// <summary>Trailing days treated as still-arriving (scored, reported, never counted).</summary>
    public required int MaturityDays { get; init; }

    /// <summary>Table-level data-quality probes, independent of any metric's model.</summary>
    public required HealthCheckDataQuality DataQuality { get; init; }

    /// <summary>One scored section per monitored metric.</summary>
    public required IReadOnlyList<HealthCheckMetricReport> Metrics { get; init; }
}

/// <summary>The scored series of one metric: its model, fit, trend, shifts, anomalies, and points.</summary>
public sealed record HealthCheckMetricReport
{
    public required string Name { get; init; }

    public required string Expression { get; init; }

    public required HealthCheckModelInfo Model { get; init; }

    /// <summary>Fit metrics over the mature actual points; null when fewer than ten exist, where the
    /// numbers would be noise.</summary>
    public HealthCheckFitMetrics? Fit { get; init; }

    /// <summary>The robust trend the expectation rides on (slope 0 for trendless or baseline-scored series).</summary>
    public required HealthCheckTrendInfo Trend { get; init; }

    /// <summary>Regime changes PELT found in the prediction residuals: backfills, purges, source onboarding.
    /// A level shift is reported once, not as a wall of daily anomalies.</summary>
    public required IReadOnlyList<HealthCheckLevelShift> LevelShifts { get; init; }

    public required HealthCheckAnomalySummary AnomalySummary { get; init; }

    /// <summary>The full scored series in date order.</summary>
    public required IReadOnlyList<HealthCheckSeriesPoint> Series { get; init; }
}

/// <summary>One scored date of one metric.</summary>
public sealed record HealthCheckSeriesPoint
{
    public required DateTime Date { get; init; }

    /// <summary>The observed metric value; 0 for a date the source had no row (or a NULL aggregate) for.</summary>
    public required double Actual { get; init; }

    /// <summary>The training label: the actual value, or the imputed estimate where the date was missing.</summary>
    public required double Adjusted { get; init; }

    /// <summary>The model's expected value for the date (trend plus calendar pattern).</summary>
    public required double Predicted { get; init; }

    /// <summary>The robust studentized deviation (|residual| in MAD-sigmas): how abnormal, not just whether.</summary>
    public required double Severity { get; init; }

    /// <summary>True when the date was absent from the source and the value was imputed.</summary>
    public required bool Imputed { get; init; }

    /// <summary>True for trailing dates whose data may still be arriving: scored, never counted.</summary>
    public required bool Immature { get; init; }

    public required bool Anomaly { get; init; }

    /// <summary>Why the point was tagged: "Missing Data", "Absolute Difference", or "Relative Difference";
    /// null for a normal point.</summary>
    public string? AnomalyReason { get; init; }
}

/// <summary>Prediction-fit metrics over the mature actual points of one metric's series.</summary>
public sealed record HealthCheckFitMetrics
{
    public required double MeanAbsoluteError { get; init; }

    public required double RootMeanSquaredError { get; init; }

    public required double MeanAbsolutePercentageError { get; init; }

    public required double RSquared { get; init; }
}

/// <summary>The model that scored one metric: where it came from and how it validated.</summary>
public sealed record HealthCheckModelInfo
{
    /// <summary>The AutoML-selected trainer, or WeekdayMedianBaseline for short histories.</summary>
    public required string Trainer { get; init; }

    /// <summary>True when this run trained the model; false when it reused the stored one.</summary>
    public required bool TrainedThisRun { get; init; }

    public required DateTime TrainedAtUtc { get; init; }

    /// <summary>Wall-clock seconds the AutoML experiment ran; null when reused or baseline.</summary>
    public double? TrainingSeconds { get; init; }

    /// <summary>AutoML trials evaluated during training; null when reused or baseline.</summary>
    public int? Trials { get; init; }

    /// <summary>The best trial's validation R-squared recorded at training time.</summary>
    public double? ValidationRSquared { get; init; }
}

/// <summary>The robust trend component of one metric's expectation.</summary>
public sealed record HealthCheckTrendInfo
{
    /// <summary>The Theil-Sen slope in metric units per day (0 for flat or baseline-scored series).</summary>
    public required double SlopePerDay { get; init; }

    /// <summary>How many trailing points the fit used.</summary>
    public required int WindowPoints { get; init; }
}

/// <summary>One regime change in a metric's residuals.</summary>
public sealed record HealthCheckLevelShift
{
    /// <summary>The first date of the new regime.</summary>
    public required DateTime Date { get; init; }

    public required double MedianBefore { get; init; }

    public required double MedianAfter { get; init; }

    /// <summary>The jump in robust sigmas: how violent the regime change was.</summary>
    public required double MagnitudeSigma { get; init; }
}

/// <summary>Anomaly counts by reason, with the affected date range, for one metric.</summary>
public sealed record HealthCheckAnomalySummary
{
    public required int Total { get; init; }

    public required int MissingData { get; init; }

    public required int AbsoluteDifference { get; init; }

    public required int RelativeDifference { get; init; }

    /// <summary>Trailing points excluded from anomaly counting because their data may still be arriving.</summary>
    public required int ImmaturePoints { get; init; }

    public DateTime? FirstAnomalyDate { get; init; }

    public DateTime? LastAnomalyDate { get; init; }
}

/// <summary>Table-level data-quality probes: the classic warehouse date-column pathologies, counted in the
/// same scan family as the series so every run reports them for free.</summary>
public sealed record HealthCheckDataQuality
{
    /// <summary>Rows dated after the as-of date: clock skew, bad source extracts, timezone arithmetic.</summary>
    public required long FutureDatedRows { get; init; }

    /// <summary>Rows dated before the sentinel floor: the 1900-01-01 placeholder family.</summary>
    public required long SentinelDatedRows { get; init; }

    /// <summary>Rows whose date column is NULL (invisible to the series, counted here).</summary>
    public required long NullDatedRows { get; init; }
}
