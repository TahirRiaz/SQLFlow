namespace SqlFlow.Yaml;

// Binding-only DTOs for the health-check document (flowType: hc). Every property is nullable; all defaults
// and validation live in YamlHealthCheckFlowLoader, which maps these into the immutable
// SqlFlow.Core.HealthChecks.HealthCheckFlow.

internal sealed class HealthCheckYaml
{
    public string? FlowType { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Batch { get; set; }
    public string? Lifecycle { get; set; }

    /// <summary>auto (default) lets schedules and batch/node group runs execute the check; manual reserves it
    /// for a direct trigger (the GUI's run button, a single-flow API trigger, or a direct CLI run).</summary>
    public string? Mode { get; set; }

    /// <summary>Each value is either a plain string (a SQL Server connection reference, the back-compatible
    /// form) or a map with 'provider' and 'connection' keys; the loader disambiguates.</summary>
    public Dictionary<string, object>? Connections { get; set; }

    public HealthCheckTargetYaml? Target { get; set; }
    public string? DateColumn { get; set; }

    /// <summary>The single-metric shorthand: an aggregate expression. Mutually exclusive with 'metrics'.</summary>
    public string? BaseValue { get; set; }

    /// <summary>The multi-metric form: every entry is computed in the same scan of the target.</summary>
    public List<HealthCheckMetricYaml>? Metrics { get; set; }

    public string? Filter { get; set; }

    /// <summary>Trailing days whose data may still be arriving (scored, never counted); default 1.</summary>
    public int? MaturityDays { get; set; }

    /// <summary>Rows dated before this count as sentinel-dated in the data-quality probe; default 1990-01-01.</summary>
    public string? SentinelDateFloor { get; set; }

    public HealthCheckMlYaml? Ml { get; set; }
    public List<string>? Holidays { get; set; }
}

internal sealed class HealthCheckMetricYaml
{
    public string? Name { get; set; }
    public string? BaseValue { get; set; }
}

internal sealed class HealthCheckTargetYaml
{
    public string? Server { get; set; }
    public string? Connection { get; set; }

    /// <summary>The provider of a direct <c>connection:</c>; SQL Server when omitted. The series query is
    /// T-SQL, so anything but mssql/azdb is rejected at parse time.</summary>
    public string? Provider { get; set; }

    public string? Object { get; set; }
}

internal sealed class HealthCheckMlYaml
{
    public int? MaxExperimentSeconds { get; set; }
    public double? AnomalyThreshold { get; set; }
    public double? EsdAlpha { get; set; }
    public double? MaxAnomalyFraction { get; set; }
    public string? Training { get; set; }
    public int? RetrainAfterDays { get; set; }
}
