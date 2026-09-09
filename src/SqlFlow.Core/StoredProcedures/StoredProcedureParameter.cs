namespace SqlFlow.Core.StoredProcedures;

/// <summary>
/// One input parameter bound to a stored-procedure flow's procedure (legacy flw.Parameter). The value is
/// either a literal (<see cref="Value"/>) or resolved at run time by executing a scalar query
/// (<see cref="SelectExp"/>), which is what makes a watermark or a run-time lookup expressible in YAML.
/// Exactly one of the two is set; the loader rejects a parameter that declares both or neither.
/// </summary>
public sealed record StoredProcedureParameter
{
    /// <summary>The parameter name without the leading '@' (legacy ParamName, which stored it with one).</summary>
    public required string Name { get; init; }

    /// <summary>The scalar query whose single value becomes the parameter, evaluated immediately before the
    /// procedure runs. Null when <see cref="Value"/> supplies a literal instead.</summary>
    public string? SelectExp { get; init; }

    /// <summary>A literal value, used instead of <see cref="SelectExp"/>. Null when a query supplies it.</summary>
    public object? Value { get; init; }

    /// <summary>The connection alias <see cref="SelectExp"/> is evaluated on (legacy ParamAltServer). Null
    /// means the procedure's own server, which is the common case.</summary>
    public string? Server { get; init; }

    /// <summary>Resolve this parameter before the others, so later <see cref="SelectExp"/> queries can
    /// reference its value as a SQL parameter (legacy PreFetch). Prefetched parameters resolve in declaration
    /// order and may not reference each other's results.</summary>
    public bool Prefetch { get; init; }

    /// <summary>The value to bind when <see cref="SelectExp"/> yields NULL or no row (legacy Defaultvalue).
    /// Null means bind DBNull.</summary>
    public object? Default { get; init; }
}
