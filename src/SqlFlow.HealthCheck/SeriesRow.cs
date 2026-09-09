using Microsoft.ML.Data;

namespace SqlFlow.HealthCheck;

/// <summary>The cadence detected from a series' observed dates (the most common gap between consecutive
/// points), driving missing-date detection. Ported unchanged from the legacy engine.</summary>
public enum DataFrequency
{
    Daily,
    BiDaily,
    Weekly,
    BiWeekly,
    Monthly,
    Quarterly,
    HalfYearly,
    Yearly,
    Irregular,
}

/// <summary>
/// One date of the monitored series as the ML pipeline sees it: the observed value, the imputation-adjusted
/// training label, and the seven calendar features the regression learns from. Mutable by design: the engine
/// stages fill it in passes (load, impute, featurize, predict, tag), exactly like the legacy DailyData.
/// Features are <see cref="float"/> because ML.NET trains on Single.
/// </summary>
public sealed class SeriesRow
{
    public DateTime Date { get; set; }

    /// <summary>The observed metric value; 0 for a date the source had no row for.</summary>
    public float BaseValue { get; set; }

    /// <summary>The model's expected value, filled by scoring.</summary>
    public float PredictedValue { get; set; }

    /// <summary>The imputation-adjusted value: the observed value, or the imputed estimate for a missing date.</summary>
    public float BaseValueAdjusted { get; set; }

    /// <summary>The ML training label: <see cref="BaseValueAdjusted"/> minus the robust trend at the date.
    /// The model learns the calendar pattern of the DETRENDED series; scoring adds the trend back, so a
    /// growing table's growth is expectation, not anomaly.</summary>
    public float DetrendedLabel { get; set; }

    /// <summary>1 when the date was absent from the source (the value is imputed), else 0.</summary>
    public float IsNoData { get; set; }

    public float Year { get; set; }

    public float Quarter { get; set; }

    public float WeekOfYear { get; set; }

    public float MonthNumber { get; set; }

    /// <summary>1 (Sunday) through 7 (Saturday), the legacy DATEPART(weekday) convention.</summary>
    public float DayOfWeekNumber { get; set; }

    public float IsWeekend { get; set; }

    public float IsHoliday { get; set; }

    public bool AnomalyDetected { get; set; }

    /// <summary>"Missing Data", "Absolute Difference", or "Relative Difference"; null for a normal point.</summary>
    public string? AnomalyReason { get; set; }

    /// <summary>The robust studentized deviation of the point (|residual| in MAD-sigmas): how abnormal,
    /// not just whether.</summary>
    public double Severity { get; set; }

    /// <summary>True for the trailing dates inside the maturity window: data may still be arriving there, so
    /// the point is scored and reported but never counted as an anomaly.</summary>
    public bool IsImmature { get; set; }
}

/// <summary>The ML.NET scoring output: the regression score is the expected metric value.</summary>
public sealed class SeriesPrediction
{
    [ColumnName("Score")]
    public float PredictedValue { get; set; }
}
