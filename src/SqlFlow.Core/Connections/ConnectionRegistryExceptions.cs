namespace SqlFlow.Core.Connections;

/// <summary>
/// Thrown when a full-mode (control-database) registry has no data source for an alias. Distinct from the
/// lightweight-mode "aliases are not supported" error so a genuine configuration mistake (a typo'd alias in a
/// real registry) is not confused with running without a control database.
/// </summary>
public sealed class DataSourceNotFoundException : SqlFlowException
{
    public DataSourceNotFoundException(string alias)
        : base($"No data source is registered for alias '{alias}' in the control database.")
        => Alias = alias;

    public string Alias { get; }
}

/// <summary>
/// Thrown when a full-mode registry has no service principal for an alias. Distinct from the lightweight-mode
/// "aliases are not supported" error so a typo'd alias in a real registry is not confused with running without a
/// control database.
/// </summary>
public sealed class ServicePrincipalNotFoundException : SqlFlowException
{
    public ServicePrincipalNotFoundException(string alias)
        : base($"No service principal is registered for alias '{alias}' in the control database.")
        => Alias = alias;

    public string Alias { get; }
}

/// <summary>
/// Thrown when a registry value that should be secretless carries a resting secret (a password keyword in a
/// literal connection string that is not a whole <c>${...}</c> reference). This is the gate ON READ: it fires
/// before the value reaches the resolver, so a row inserted with the constraint bypassed, or predating the
/// constraint, is still caught.
/// </summary>
public sealed class SecretlessViolationException : SqlFlowException
{
    public SecretlessViolationException(string alias, string column)
        : base($"Data source '{alias}' has a resting secret in '{column}'. Store a passwordless connection string or a ${{...}} reference instead.")
    {
        Alias = alias;
        Column = column;
    }

    public string Alias { get; }

    public string Column { get; }
}
