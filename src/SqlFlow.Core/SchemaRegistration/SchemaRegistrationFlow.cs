namespace SqlFlow.Core.SchemaRegistration;

/// <summary>
/// A schema registration flow (flowType: sch): registers the simple objects of one external SQL Server database
/// (its tables and views, with their columns, primary keys, and scripts) in the shadow catalog, so a subscriber
/// that reads that database can be resolved onto real objects and a question about it knows which connection and
/// database to run against. It is a metadata fetch only: nothing it registers is ever loaded, and it takes part in
/// no execution wave. How the objects join and what is computed over them is the subscriber's (the dashboard's)
/// knowledge, not this flow's, so it deliberately reads no foreign keys and parses no view bodies.
/// </summary>
public sealed record SchemaRegistrationFlow
{
    /// <summary>The stable, name-derived flow id (logs and artifacts key on it without a control database).</summary>
    public required int FlowId { get; init; }

    /// <summary>The flow name; the registration's identity and run-history folder.</summary>
    public required string SysAlias { get; init; }

    /// <summary>The flow's declared lifecycle (the YAML <c>lifecycle:</c>, production by default).</summary>
    public Runs.FlowLifecycle Lifecycle { get; init; } = Runs.FlowLifecycle.Production;

    public string? Description { get; init; }

    /// <summary>The grouping label: a filter for listing and triggering, with no effect on scheduling.</summary>
    public string? Batch { get; init; }

    /// <summary>The name of the connection (declared under <c>connections:</c>) for the database to register.</summary>
    public required string Server { get; init; }

    /// <summary>The connection reference the resolver expects: the declared connection addressed as an
    /// <c>@alias</c> (the same convention every other flow uses).</summary>
    public string ConnectionReference => "@" + Server;

    /// <summary>What is registered from the database.</summary>
    public SchemaRegistrationScope Scope { get; init; } = new();
}

/// <summary>
/// Which part of a database a schema registration reads: an optional explicit database (null means the connection's
/// own default catalog) and optional schema filters. Schema names compare case-insensitively; an empty include list
/// means every schema, and the exclude list is applied after it.
/// </summary>
public sealed record SchemaRegistrationScope
{
    /// <summary>The database to register; null registers the connection's default catalog (<c>DB_NAME()</c>).</summary>
    public string? Database { get; init; }

    /// <summary>When non-empty, only objects in these schemas are registered.</summary>
    public IReadOnlyList<string> IncludeSchemas { get; init; } = [];

    /// <summary>Objects in these schemas are never registered, applied after <see cref="IncludeSchemas"/>.</summary>
    public IReadOnlyList<string> ExcludeSchemas { get; init; } = [];

    /// <summary>Whether an object in <paramref name="schema"/> is inside this scope.</summary>
    public bool Includes(string schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (IncludeSchemas.Count > 0 && !IncludeSchemas.Contains(schema, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return !ExcludeSchemas.Contains(schema, StringComparer.OrdinalIgnoreCase);
    }
}
