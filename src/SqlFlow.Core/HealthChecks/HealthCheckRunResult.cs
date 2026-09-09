using SqlFlow.Core.Ingestion;

namespace SqlFlow.Core.HealthChecks;

/// <summary>The per-metric summary inside <see cref="HealthCheckRunResult"/> (run.json carries these; the
/// full scored series rides in the healthcheck.json report, so the two artifacts do not duplicate).</summary>
public sealed record HealthCheckMetricResult
{
    public required string Name { get; init; }

    /// <summary>Dates in the scored series (observed plus imputed).</summary>
    public required int SeriesPoints { get; init; }

    /// <summary>Dates absent from the source whose value was imputed.</summary>
    public required int ImputedPoints { get; init; }

    /// <summary>Trailing points excluded from anomaly counting (data may still be arriving).</summary>
    public required int ImmaturePoints { get; init; }

    public required int Anomalies { get; init; }

    /// <summary>Regime changes PELT reported in this metric's residuals.</summary>
    public required int LevelShifts { get; init; }

    /// <summary>True when this run trained the metric's model; false when it scored with the stored one.</summary>
    public required bool ModelTrained { get; init; }

    public required string ModelTrainer { get; init; }

    /// <summary>Fit metrics over the mature actual points; null with fewer than ten of them.</summary>
    public HealthCheckFitMetrics? Fit { get; init; }

    /// <summary>Why this metric could not be scored (the run fails, the other metrics still report);
    /// null when it scored.</summary>
    public string? Error { get; init; }
}

/// <summary>The outcome of executing one health-check flow: the run.json summary.</summary>
public sealed record HealthCheckRunResult
{
    public required Guid RunId { get; init; }

    public required int FlowId { get; init; }

    public required bool Success { get; init; }

    public DateTime StartTimeUtc { get; init; }

    public DateTime EndTimeUtc { get; init; }

    public int DurationSeconds { get; init; }

    /// <summary>The SQL the run executed (the series and data-quality queries), in execution order. Always
    /// captured, success or failure: the generated SQL is the debugging record.</summary>
    public IReadOnlyList<SqlTraceEntry> SqlTrace { get; init; } = [];

    /// <summary>The failure message (already redacted of any secret), or null on success.</summary>
    public string? Error { get; init; }

    /// <summary>The detected cadence of the shared date axis, null when the run failed before detection.</summary>
    public string? Frequency { get; init; }

    /// <summary>Table-level data-quality probes, null when the run failed before probing.</summary>
    public HealthCheckDataQuality? DataQuality { get; init; }

    /// <summary>One summary per monitored metric.</summary>
    public IReadOnlyList<HealthCheckMetricResult> MetricResults { get; init; } = [];

    /// <summary>Anomalies across every metric (the CI gate number).</summary>
    public int TotalAnomalies { get; init; }
}

/// <summary>What a health-check run hands back: the summary result (run.json) plus the full scored report
/// (healthcheck.json). <see cref="Report"/> is null only when the run failed before the series was scored.</summary>
public sealed record HealthCheckRunOutcome
{
    public required HealthCheckRunResult Result { get; init; }

    public HealthCheckReport? Report { get; init; }
}
