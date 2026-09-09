using SqlFlow.Core;
using SqlFlow.Core.HealthChecks;

namespace SqlFlow.Yaml;

/// <summary>
/// The shared mapping/validation vocabulary of a health-check declaration, used by both authoring surfaces:
/// the standalone document (flowType: hc) and the embedded <c>healthCheck:</c> block of an ingestion flow.
/// Every helper takes a <c>prefix</c> ("" for the standalone document, "healthCheck." for the
/// embedded block) so error messages always name the exact YAML path the author wrote.
/// </summary>
internal static class YamlHealthCheckParts
{
    public const int MaxExperimentSecondsCeiling = 86_400;

    /// <summary>The validated <c>ml:</c> block with its documented defaults applied.</summary>
    public sealed record MlSettings(
        int MaxExperimentSeconds,
        double AnomalyThreshold,
        double EsdAlpha,
        double MaxAnomalyFraction,
        HealthCheckTraining Training,
        int? RetrainAfterDays);

    /// <summary>Validates the <c>ml:</c> block (an absent block yields all defaults).</summary>
    public static MlSettings MapMl(HealthCheckMlYaml? mlYaml, string source, string prefix = "")
    {
        var ml = mlYaml ?? new HealthCheckMlYaml();
        var training = ParseTraining(ml.Training, source, prefix);

        var maxExperimentSeconds = ml.MaxExperimentSeconds ?? 120;
        if (maxExperimentSeconds < 1 || maxExperimentSeconds > MaxExperimentSecondsCeiling)
        {
            throw new FlowValidationException(
                $"{source}: '{prefix}ml.maxExperimentSeconds' must be between 1 and {MaxExperimentSecondsCeiling}, got {maxExperimentSeconds}.");
        }

        var anomalyThreshold = ml.AnomalyThreshold ?? 2.0;
        if (!double.IsFinite(anomalyThreshold) || anomalyThreshold <= 0 || anomalyThreshold > 100)
        {
            throw new FlowValidationException(
                $"{source}: '{prefix}ml.anomalyThreshold' must be a positive number of standard deviations (at most 100), got {ml.AnomalyThreshold}.");
        }

        var esdAlpha = ml.EsdAlpha ?? 0.025;
        if (!double.IsFinite(esdAlpha) || esdAlpha <= 0 || esdAlpha >= 0.5)
        {
            throw new FlowValidationException(
                $"{source}: '{prefix}ml.esdAlpha' must be a significance level strictly between 0 and 0.5, got {ml.EsdAlpha}.");
        }

        var maxAnomalyFraction = ml.MaxAnomalyFraction ?? 0.10;
        if (!double.IsFinite(maxAnomalyFraction) || maxAnomalyFraction <= 0 || maxAnomalyFraction > 0.49)
        {
            throw new FlowValidationException(
                $"{source}: '{prefix}ml.maxAnomalyFraction' must be in (0, 0.49], got {ml.MaxAnomalyFraction}.");
        }

        if (ml.RetrainAfterDays is { } retrainAfterDays)
        {
            if (retrainAfterDays < 1)
            {
                throw new FlowValidationException(
                    $"{source}: '{prefix}ml.retrainAfterDays' must be at least 1, got {retrainAfterDays}.");
            }

            if (training != HealthCheckTraining.Auto)
            {
                throw new FlowValidationException(
                    $"{source}: '{prefix}ml.retrainAfterDays' only applies with '{prefix}ml.training: auto' " +
                    $"('{training.ToString().ToLowerInvariant()}' ignores model age).");
            }
        }

        return new MlSettings(maxExperimentSeconds, anomalyThreshold, esdAlpha, maxAnomalyFraction, training, ml.RetrainAfterDays);
    }

    /// <summary>Validates <c>maturityDays</c> (default 1; the trailing still-arriving window).</summary>
    public static int MapMaturityDays(int? value, string source, string prefix = "")
    {
        var maturityDays = value ?? 1;
        if (maturityDays < 0 || maturityDays > 30)
        {
            throw new FlowValidationException(
                $"{source}: '{prefix}maturityDays' must be between 0 and 30, got {maturityDays}.");
        }

        return maturityDays;
    }

    /// <summary>Maps the monitored metrics: the <c>metrics:</c> list, or the single-metric <c>baseValue:</c>
    /// shorthand (named by convention). Names key state folders and report sections, so they are validated
    /// and deduplicated here.</summary>
    public static List<HealthCheckMetric> MapMetrics(
        string? baseValue, List<HealthCheckMetricYaml>? list, string source, string prefix = "")
    {
        var shorthand = YamlDocumentParts.NullIfBlank(baseValue)?.Trim();

        if (shorthand is not null && list is { Count: > 0 })
        {
            throw new FlowValidationException(
                $"{source}: set either '{prefix}baseValue' (one metric) or '{prefix}metrics' (a list), not both.");
        }

        if (shorthand is not null)
        {
            return [new HealthCheckMetric { Name = HealthCheckMetric.DefaultName(shorthand), Expression = shorthand }];
        }

        if (list is null || list.Count == 0)
        {
            throw new FlowValidationException(
                $"{source}: a health check needs a metric. Set '{prefix}baseValue' (an aggregate like COUNT(*) or SUM(Amount)) " +
                $"or a '{prefix}metrics' list of {{name, baseValue}} entries.");
        }

        var metrics = new List<HealthCheckMetric>(list.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < list.Count; i++)
        {
            var expression = YamlDocumentParts.NullIfBlank(list[i].BaseValue)?.Trim()
                ?? throw new FlowValidationException($"{source}: '{prefix}metrics[{i}].baseValue' is required (an aggregate expression).");

            var metricName = YamlDocumentParts.NullIfBlank(list[i].Name)?.Trim() ?? HealthCheckMetric.DefaultName(expression);
            if (!IsValidMetricName(metricName))
            {
                throw new FlowValidationException(
                    $"{source}: '{prefix}metrics[{i}].name' '{metricName}' is invalid. Use letters, digits, '_', or '-'.");
            }

            if (!seen.Add(metricName))
            {
                throw new FlowValidationException($"{source}: metric name '{metricName}' is declared more than once.");
            }

            metrics.Add(new HealthCheckMetric { Name = metricName, Expression = expression });
        }

        return metrics;
    }

    /// <summary>Parses and de-duplicates the <c>holidays:</c> list (calendar features).</summary>
    public static List<DateOnly> MapHolidays(List<string>? holidays, string source, string prefix = "")
    {
        var parsed = new List<DateOnly>();
        if (holidays is null)
        {
            return parsed;
        }

        var seen = new HashSet<DateOnly>();
        for (var i = 0; i < holidays.Count; i++)
        {
            var date = YamlDocumentParts.ParseDate(holidays[i], $"{prefix}holidays[{i}]", source)
                ?? throw new FlowValidationException($"{source}: '{prefix}holidays[{i}]' must not be blank.");

            // A duplicate date is harmless to the features; keeping one copy keeps the model deterministic.
            if (seen.Add(date))
            {
                parsed.Add(date);
            }
        }

        return parsed;
    }

    private static bool IsValidMetricName(string name)
        => name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static HealthCheckTraining ParseTraining(string? value, string source, string prefix)
        => value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "auto" => HealthCheckTraining.Auto,
            "always" => HealthCheckTraining.Always,
            "never" => HealthCheckTraining.Never,
            _ => throw new FlowValidationException(
                $"{source}: '{prefix}ml.training' has unknown value '{value}'. Allowed: auto, always, never."),
        };
}
