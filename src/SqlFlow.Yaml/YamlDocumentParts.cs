using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Runs;

namespace SqlFlow.Yaml;

/// <summary>
/// The shared mapping/validation vocabulary of the relational flow documents (flowType: ing / exp / sp): the
/// <c>connections:</c> block, endpoint-to-connection resolution, qualified object names, dates, and the stable
/// name-derived flow id. Every loader goes through these helpers so the YAML dialect stays one dialect: the
/// same connection forms, the same provider tokens, and the same error wording in every document kind.
/// </summary>
internal static class YamlDocumentParts
{
    /// <summary>Parses a <c>mode:</c> value (auto | manual | disabled; blank/absent is auto). One vocabulary for
    /// every place a definition opts out of automatic execution: the document envelope of every flow kind, a
    /// health-check flow, and an ingestion assertion. <c>manual</c> reserves a working definition for direct
    /// triggers; <c>disabled</c> deactivates a retired one, with the same automatic-execution exclusion and the
    /// retirement carried visibly on the pipeline row.</summary>
    public static ExecutionMode ParseExecutionMode(string? value, string property, string source)
        => value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "auto" => ExecutionMode.Auto,
            "manual" => ExecutionMode.Manual,
            "disabled" => ExecutionMode.Disabled,
            _ => throw new FlowValidationException(
                $"{source}: '{property}' has unknown value '{value}'. Allowed: auto, manual, disabled."),
        };

    /// <summary>Parses a <c>lifecycle:</c> value (production | development; blank/absent is production). One
    /// vocabulary for every document kind: a flow under active development declares <c>lifecycle: development</c>
    /// and stops generating notification events, while execution itself is unaffected. Production is the default
    /// so an existing estate (which declares nothing) keeps alerting exactly as before.</summary>
    public static FlowLifecycle ParseLifecycle(string? value, string source)
        => value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "production" => FlowLifecycle.Production,
            "development" => FlowLifecycle.Development,
            _ => throw new FlowValidationException(
                $"{source}: 'lifecycle' has unknown value '{value}'. Allowed: production, development."),
        };

    /// <summary>Maps the document's <c>connections:</c> block. Each value is either a plain string (SQL Server,
    /// the back-compatible form), a map with 'provider' and 'connection' keys, or NOTHING: a bare alias
    /// resolves <c>${env:SQLFLOW_CONN_&lt;NAME&gt;}</c> by the canonical convention, so an enterprise document
    /// can stay entirely reference-free (see docs/environment-variables.md).</summary>
    public static Dictionary<string, DataSource> MapConnections(Dictionary<string, object>? block, string source)
    {
        var connections = new Dictionary<string, DataSource>(StringComparer.OrdinalIgnoreCase);
        if (block is null)
        {
            return connections;
        }

        foreach (var (rawName, node) in block)
        {
            var connectionName = rawName?.Trim() ?? string.Empty;
            if (!IsValidConnectionName(connectionName))
            {
                throw new FlowValidationException(
                    $"{source}: connection name '{rawName}' is invalid. Use letters, digits, '_', '.', or '-'.");
            }

            var (reference, kind) = ParseConnectionNode(node, connectionName, source);
            if (!connections.TryAdd(connectionName, BuildDataSource(connectionName, reference, kind)))
            {
                throw new FlowValidationException($"{source}: connection '{connectionName}' is declared more than once.");
            }
        }

        return connections;
    }

    private static (string Reference, DataSourceKind Kind) ParseConnectionNode(object? node, string connectionName, string source)
    {
        switch (node)
        {
            // The canonical convention: a bare alias resolves its own well-known environment variable, so
            // a document can declare WHICH connections it needs without carrying any reference at all.
            case null:
            case string blank when string.IsNullOrWhiteSpace(blank):
                return (ConventionalConnectionReference(connectionName), DataSourceKind.MSSQL);

            case string reference:
                return (reference.Trim(), DataSourceKind.MSSQL);

            case IDictionary<object, object?> map:
            {
                string? provider = null;
                string? reference = null;
                foreach (var (key, value) in map)
                {
                    switch ((key as string)?.Trim().ToLowerInvariant())
                    {
                        case "provider":
                            provider = value as string;
                            break;
                        case "connection":
                            reference = value as string;
                            break;
                    }
                }

                // The map form without a 'connection:' keeps the provider and takes the convention.
                var resolved = string.IsNullOrWhiteSpace(reference)
                    ? ConventionalConnectionReference(connectionName)
                    : reference.Trim();
                return (resolved, ParseProvider(provider, $"connections.{connectionName}.provider", source));
            }

            default:
                throw new FlowValidationException(
                    $"{source}: connection '{connectionName}' must be a connection string, a ${{...}} reference, " +
                    "a map with 'provider' and 'connection', or bare (which resolves " +
                    $"'{ConventionalConnectionReference(connectionName)}' by convention).");
        }
    }

    /// <summary>The canonical reference of a bare-declared connection; the rule itself lives in
    /// <see cref="ConnectionConvention"/>, shared with the hygiene warning.</summary>
    public static string ConventionalConnectionReference(string connectionName)
        => ConnectionConvention.Reference(connectionName);

    public static DataSourceKind ParseProvider(string? provider, string field, string source)
        => provider?.Trim().ToLowerInvariant() switch
        {
            null or "" or "mssql" or "sqlserver" => DataSourceKind.MSSQL,
            "azdb" => DataSourceKind.AZDB,
            "mysql" => DataSourceKind.MySQL,
            "postgres" or "postgresql" => DataSourceKind.PostgreSQL,
            "oracle" => DataSourceKind.Oracle,
            _ => throw new FlowValidationException(
                $"{source}: '{field}' has unknown provider '{provider}'. Allowed: mssql, azdb, mysql, postgres, oracle."),
        };

    /// <summary>Resolves an endpoint to the connection NAME the model carries. The endpoint either references a
    /// declared connection by <c>server:</c> or carries a direct <c>connection:</c>, which is registered under
    /// the synthesized name.</summary>
    public static string ResolveEndpointConnection(
        string? server, string? connection, string? provider, string section, string syntheticName,
        Dictionary<string, DataSource> connections, string source)
    {
        var hasServer = !string.IsNullOrWhiteSpace(server);
        var hasConnection = !string.IsNullOrWhiteSpace(connection);

        if (hasServer && hasConnection)
        {
            throw new FlowValidationException(
                $"{source}: '{section}' sets both 'server' and 'connection'; use exactly one.");
        }

        if (hasServer)
        {
            var serverName = server!.Trim();
            if (!connections.ContainsKey(serverName))
            {
                throw new FlowValidationException(
                    $"{source}: '{section}.server' references '{serverName}', which is not declared under 'connections:'.");
            }

            return serverName;
        }

        if (!hasConnection)
        {
            throw new FlowValidationException(
                $"{source}: '{section}' needs a connection. Set '{section}.connection' to a connection string or " +
                $"a ${{...}} reference, or '{section}.server' to a name declared under 'connections:'.");
        }

        if (connections.ContainsKey(syntheticName))
        {
            throw new FlowValidationException(
                $"{source}: '{section}.connection' is set, but 'connections:' already declares '{syntheticName}'. " +
                $"Either rename that connection or use '{section}.server: {syntheticName}'.");
        }

        var kind = ParseProvider(provider, $"{section}.provider", source);
        connections.Add(syntheticName, BuildDataSource(syntheticName, connection!.Trim(), kind));
        return syntheticName;
    }

    public static DataSource BuildDataSource(string name, string reference, DataSourceKind kind)
        => new()
        {
            Alias = name,
            Kind = kind,
            ConnectionRef = reference,
            Credential = new CredentialProfile { Mode = CredentialMode.InlineConnectionString },
        };

    /// <summary>Rejects a foreign-provider connection where the engine requires SQL Server, at parse time rather
    /// than deep in the run. <paramref name="role"/> is the endpoint word ('source', 'target', 'procedure');
    /// <paramref name="requirement"/> reads like "an ingestion flow's target".</summary>
    public static void RequireSqlServerConnection(
        Dictionary<string, DataSource> connections, string connectionName, string role, string requirement, string source)
    {
        var kind = connections[connectionName].Kind;
        if (kind is not (DataSourceKind.MSSQL or DataSourceKind.AZDB))
        {
            throw new FlowValidationException(
                $"{source}: the {role} connection '{connectionName}' is '{kind}'; {requirement} must be SQL Server (mssql or azdb).");
        }
    }

    /// <summary>Parses a qualified object name through <see cref="RelationalObject.Parse"/>, rewrapping the
    /// model's error with the YAML field that carried the value.</summary>
    public static RelationalObject ParseQualifiedObject(string raw, string field, string source)
    {
        try
        {
            return RelationalObject.Parse(raw);
        }
        catch (SqlFlowException ex)
        {
            throw new FlowValidationException($"{source}: '{field}': {ex.Message}", ex);
        }
    }

    public static DateOnly? ParseDate(string? value, string field, string source)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateOnly.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new FlowValidationException($"{source}: '{field}' must be a date like 2024-01-31, got '{value}'.");
    }

    /// <summary>The document's required <c>name:</c>, which becomes SysAlias and seeds the stable flow id.</summary>
    public static string RequireFlowName(string? name, string kindLabel, string source)
        => NullIfBlank(name)?.Trim()
            ?? throw new FlowValidationException($"{source}: 'name' is required for {kindLabel}.");

    /// <summary>A stable positive flow id derived from the flow name (the deterministic name-based identity),
    /// so logs and staging tables key consistently across runs without a database to assign ids.</summary>
    public static int StableFlowId(string name)
        => BitConverter.ToInt32(FlowIdentity.FromName(name).ToByteArray(), 0) & 0x7FFFFFFF;

    public static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool IsValidConnectionName(string name)
        => name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');
}
