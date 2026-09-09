namespace SqlFlow.Core.Connections;

/// <summary>
/// THE canonical mapping between a document's connection name and its well-known environment variable:
/// <c>SQLFLOW_CONN_&lt;NAME&gt;</c>, uppercased, every non-alphanumeric character folded to '_'. One rule for
/// every document kind and every platform .NET runs on (names are strictly uppercase because environment
/// variables are case-sensitive on Linux). The YAML loaders apply it to bare aliases; the secret-hygiene
/// warning names it; docs/environment-variables.md documents it. Nothing else may restate it.
/// </summary>
public static class ConnectionConvention
{
    /// <summary>The environment variable name of a connection: <c>SQLFLOW_CONN_&lt;NAME&gt;</c>.</summary>
    public static string EnvironmentVariable(string connectionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        var sanitized = new string(connectionName.Trim().ToUpperInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());
        return "SQLFLOW_CONN_" + sanitized;
    }

    /// <summary>The resolvable reference form: <c>${env:SQLFLOW_CONN_&lt;NAME&gt;}</c>.</summary>
    public static string Reference(string connectionName)
        => "${env:" + EnvironmentVariable(connectionName) + "}";
}
