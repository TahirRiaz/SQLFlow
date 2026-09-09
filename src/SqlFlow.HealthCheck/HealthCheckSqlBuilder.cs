using System.Globalization;
using System.Text;
using SqlFlow.Core.HealthChecks;

namespace SqlFlow.HealthCheck;

/// <summary>
/// Builds the two statements a health check runs. The series SELECT computes EVERY monitored metric in one
/// scan of the target (the legacy flw.GetRVHealthCheck shape, widened to N value columns), bounded to the
/// sane calendar window [sentinel floor .. as-of]: a single 1900-01-01 placeholder or future-dated row would
/// otherwise stretch the expected-date walk across decades and drown the analysis in imputed points. The rows
/// outside that window are not ignored, they are exactly what the data-quality SELECT counts (future,
/// sentinel, NULL dates), so every run reports the pathologies without letting them poison the series.
/// </summary>
public static class HealthCheckSqlBuilder
{
    /// <summary>The series SELECT for <paramref name="flow"/>: one [Date] column plus one value column per
    /// metric, in the flow's metric order, bounded to [sentinel floor .. <paramref name="asOfDate"/>].</summary>
    public static string SeriesSelect(HealthCheckFlow flow, DateTime asOfDate)
    {
        ArgumentNullException.ThrowIfNull(flow);

        var dateColumn = Bracket(flow.DateColumn);
        var floor = flow.SentinelDateFloor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var asOf = asOfDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        sb.Append("SELECT ").Append(dateColumn).Append(" AS [Date]");
        foreach (var metric in flow.Metrics)
        {
            sb.Append(", CAST(").Append(metric.Expression.Trim()).Append(" AS float) AS ").Append(Bracket(metric.Name));
        }

        sb.AppendLine();
        sb.Append("FROM ").AppendLine(flow.Target.QualifiedName);
        sb.AppendLine("WHERE 1 = 1");
        sb.Append("  AND ").Append(dateColumn).Append(" >= '").Append(floor).Append('\'').AppendLine();
        sb.Append("  AND ").Append(dateColumn).Append(" <= '").Append(asOf).Append('\'');
        if (!string.IsNullOrWhiteSpace(flow.FilterCriteria))
        {
            sb.AppendLine().Append("  AND (").Append(flow.FilterCriteria.Trim()).Append(')');
        }

        sb.AppendLine();
        sb.Append("GROUP BY ").AppendLine(dateColumn);
        sb.Append("ORDER BY ").Append(dateColumn).Append(';');
        return sb.ToString();
    }

    /// <summary>The data-quality SELECT: future-dated rows (after <paramref name="asOfDate"/>),
    /// sentinel-dated rows (before the flow's floor), and NULL-dated rows, under the same filter as the
    /// series so the probes describe the monitored slice.</summary>
    public static string DataQualitySelect(HealthCheckFlow flow, DateTime asOfDate)
    {
        ArgumentNullException.ThrowIfNull(flow);

        var dateColumn = Bracket(flow.DateColumn);
        var asOf = asOfDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var floor = flow.SentinelDateFloor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var filter = string.IsNullOrWhiteSpace(flow.FilterCriteria)
            ? string.Empty
            : $"{Environment.NewLine}  AND ({flow.FilterCriteria.Trim()})";

        return
            $"SELECT{Environment.NewLine}" +
            $"  COUNT_BIG(CASE WHEN {dateColumn} > '{asOf}' THEN 1 END) AS [FutureDatedRows],{Environment.NewLine}" +
            $"  COUNT_BIG(CASE WHEN {dateColumn} < '{floor}' THEN 1 END) AS [SentinelDatedRows],{Environment.NewLine}" +
            $"  COUNT_BIG(CASE WHEN {dateColumn} IS NULL THEN 1 END) AS [NullDatedRows]{Environment.NewLine}" +
            $"FROM {flow.Target.QualifiedName}{Environment.NewLine}" +
            $"WHERE 1 = 1{filter};";
    }

    private static string Bracket(string identifier)
        => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
}
